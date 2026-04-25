using System;
using System.Collections.Generic;
using System.Linq;
using Manager;
using Newtonsoft.Json;
using SolarExpanse.Multiplayer.Networking.Client;
using SolarExpanse.Multiplayer.Networking.Host;
using SolarExpanse.Multiplayer.Networking.Protocol;
using ScriptableObjectScripts;
using UnityEngine;

using GameCompany = global::Game.Company;
using GameObjectInfo = global::Game.Info.ObjectInfo;
using ObjectInfoDataType = global::Game.ObjectInfoDataScripts.ObjectInfoData;
using FacilityType = global::Game.ObjectInfoDataScripts.Facility;
using RowResourcesDataType = global::Game.UI.Windows.Elements.ObjectInfoElements.RowResourcesData;

namespace SolarExpanse.Multiplayer.Game.Company;

public sealed class CompanyStateSyncService
{
    private readonly CompanyOwnershipService _companyOwnershipService;
    private readonly Dictionary<int, CompanyStateSnapshotMessage> _latestSnapshots = new Dictionary<int, CompanyStateSnapshotMessage>();
    private readonly Dictionary<int, string> _lastAppliedFingerprints = new Dictionary<int, string>();

    private float _nextSendAt;
    private float _nextRemotePrivateClearAt;
    private string? _lastSentFingerprint;

    public CompanyStateSyncService(CompanyOwnershipService companyOwnershipService)
    {
        _companyOwnershipService = companyOwnershipService;
    }

    public IReadOnlyDictionary<int, CompanyStateSnapshotMessage> LatestSnapshots => _latestSnapshots;

    public void TickHost(DirectConnectHost host, int localCompanySlot, string localPlayerName)
    {
        if (!ShouldSend(localCompanySlot))
        {
            return;
        }

        var snapshot = CaptureLocalSnapshot(localCompanySlot, localPlayerName, force: false);
        if (snapshot == null)
        {
            return;
        }

        HandleIncomingSnapshot(snapshot);
        ApplyRemoteSnapshots(localCompanySlot);
        host.Broadcast(CreatePublicSnapshot(snapshot));
    }

    public void TickClient(DirectConnectClient client, int localCompanySlot, string localPlayerName)
    {
        if (!ShouldSend(localCompanySlot))
        {
            return;
        }

        var snapshot = CaptureLocalSnapshot(localCompanySlot, localPlayerName, force: false);
        if (snapshot == null)
        {
            return;
        }

        HandleIncomingSnapshot(snapshot);
        client.Send(CreatePublicSnapshot(snapshot));
    }

    public void HandleIncomingSnapshot(CompanyStateSnapshotMessage snapshot)
    {
        _latestSnapshots[snapshot.CompanySlot] = snapshot;
    }

    public CompanyStateSnapshotMessage? CaptureForcedLocalSnapshot(int localCompanySlot, string localPlayerName)
    {
        return CaptureLocalSnapshot(localCompanySlot, localPlayerName, force: true);
    }

    public static CompanyStateSnapshotMessage CreatePublicSnapshot(CompanyStateSnapshotMessage snapshot)
    {
        return new CompanyStateSnapshotMessage
        {
            MessageType = nameof(CompanyStateSnapshotMessage),
            CompanySlot = snapshot.CompanySlot,
            CompanyId = snapshot.CompanyId,
            CompanyName = snapshot.CompanyName,
            OwnerPlayerName = snapshot.OwnerPlayerName,
            OwnedInventories = snapshot.OwnedInventories
                .Select(inventory => new ObjectInventorySnapshotDto
                {
                    ObjectId = inventory.ObjectId,
                    ObjectName = inventory.ObjectName,
                    Facilities = inventory.Facilities
                        .Where(facility => facility.Quantity > 0)
                        .Select(facility => new FacilitySnapshotDto
                        {
                            DescriptorId = facility.DescriptorId,
                            Quantity = facility.Quantity,
                            Enabled = facility.Enabled,
                            HaveWorkers = facility.HaveWorkers,
                            ValidCanAddFacility = facility.ValidCanAddFacility
                        })
                        .ToList()
                })
                .Where(inventory => inventory.Facilities.Count > 0)
                .ToList()
        };
    }

    public void ApplyRemoteSnapshots(int localCompanySlot)
    {
        var shouldClearPrivateState = UnityEngine.Time.unscaledTime >= _nextRemotePrivateClearAt;
        foreach (var snapshot in _latestSnapshots.Values.OrderBy(x => x.CompanySlot))
        {
            if (snapshot.CompanySlot == localCompanySlot)
            {
                continue;
            }

            if (!_companyOwnershipService.TryGetCompanyBySlot(snapshot.CompanySlot, out var company) || company == null)
            {
                continue;
            }

            _companyOwnershipService.SetCompanyDisplayName(snapshot.CompanySlot, snapshot.CompanyName);
            _companyOwnershipService.ApplyDisplayNamesToGame();
            if (shouldClearPrivateState)
            {
                ClearRemoteCompanyPrivateState(company);
            }

            var fingerprint = JsonConvert.SerializeObject(snapshot);
            if (_lastAppliedFingerprints.TryGetValue(snapshot.CompanySlot, out var lastFingerprint) &&
                string.Equals(lastFingerprint, fingerprint, StringComparison.Ordinal))
            {
                continue;
            }

            if (!shouldClearPrivateState)
            {
                ClearRemoteCompanyPrivateState(company);
            }

            ApplyInventoryState(company, snapshot);
            _lastAppliedFingerprints[snapshot.CompanySlot] = fingerprint;
        }

        if (shouldClearPrivateState)
        {
            _nextRemotePrivateClearAt = UnityEngine.Time.unscaledTime + 0.5f;
        }
    }

    private bool ShouldSend(int localCompanySlot)
    {
        if (localCompanySlot < 0)
        {
            return false;
        }

        if (UnityEngine.Time.unscaledTime < _nextSendAt)
        {
            return false;
        }

        _nextSendAt = UnityEngine.Time.unscaledTime + 1f;
        return true;
    }

    private CompanyStateSnapshotMessage? CaptureLocalSnapshot(int localCompanySlot, string localPlayerName, bool force)
    {
        if (!_companyOwnershipService.TryGetCompanyBySlot(localCompanySlot, out var company) || company == null)
        {
            return null;
        }

        var snapshot = new CompanyStateSnapshotMessage
        {
            MessageType = nameof(CompanyStateSnapshotMessage),
            CompanySlot = localCompanySlot,
            CompanyId = company.ID ?? string.Empty,
            CompanyName = _companyOwnershipService.TryGetCompanyDisplayName(localCompanySlot, out var displayName)
                ? displayName
                : company.name ?? company.ID ?? $"Company {localCompanySlot}",
            OwnerPlayerName = localPlayerName,
            Money = company.MoneyController?.CurrentMoney ?? 0,
            TotalProfit = company.MoneyController?.TotalProfit ?? 0,
            DiscoveredSystemsCount = company.DiscoveredSystemsCount
        };

        PopulateResearch(snapshot, company);
        PopulateInventories(snapshot, company);

        var fingerprint = JsonConvert.SerializeObject(snapshot);
        if (!force && string.Equals(_lastSentFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        _lastSentFingerprint = fingerprint;
        return snapshot;
    }

    private static void PopulateResearch(CompanyStateSnapshotMessage snapshot, GameCompany company)
    {
        var researchManager = company.ResearchManager;
        if (researchManager == null)
        {
            return;
        }

        var researchData = researchManager.GetDataToSave();
        if (researchData == null)
        {
            return;
        }

        if (researchData.completeResearch != null)
        {
            snapshot.CompletedResearchIds = researchData.completeResearch
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.id))
                .Select(x => x.id)
                .Distinct()
                .OrderBy(x => x)
                .ToList();
        }

        snapshot.CompletedResearchCount = snapshot.CompletedResearchIds.Count;

        var activeResearch = new List<ResearchProgressDto>();

        AddProgress(activeResearch, researchData.slot1);
        AddProgress(activeResearch, researchData.slot2);
        AddProgress(activeResearch, researchData.slot3);

        if (researchData.startNotFinish != null)
        {
            foreach (var item in researchData.startNotFinish)
            {
                AddProgress(activeResearch, item);
            }
        }

        snapshot.ActiveResearch = activeResearch
            .GroupBy(x => x.ResearchId)
            .Select(x => x.OrderByDescending(y => y.Progress).First())
            .OrderByDescending(x => x.Progress)
            .ThenBy(x => x.ResearchId)
            .ToList();
    }

    private static void AddProgress(List<ResearchProgressDto> activeResearch, ResearchDataProgress? progress)
    {
        if (progress == null)
        {
            return;
        }

        var researchId = progress.ResearchDefinition?.ID
                         ?? string.Empty;

        if (string.IsNullOrWhiteSpace(researchId))
        {
            return;
        }

        activeResearch.Add(new ResearchProgressDto
        {
            ResearchId = researchId,
            Progress = progress.Progress,
            Complete = progress.Complete
        });
    }

    private static void PopulateInventories(CompanyStateSnapshotMessage snapshot, GameCompany company)
    {
        var objectInfos = UnityEngine.Object.FindObjectsOfType<GameObjectInfo>();
        foreach (var objectInfo in objectInfos)
        {
            var objectInfoData = objectInfo.ObjectsInfoData?.FirstOrDefault(x => x != null && x.company == company);
            if (objectInfoData == null)
            {
                continue;
            }

            var inventory = new ObjectInventorySnapshotDto
            {
                ObjectId = objectInfo.id,
                ObjectName = objectInfo.ObjectName ?? $"Object {objectInfo.id}",
                Facilities = objectInfoData.ListFacility?
                    .Where(x => x != null && x.facilityDescriptor != null && x.FinishConstructionBool && x.Quantity > 0)
                    .GroupBy(x => x.facilityDescriptor.ID)
                    .Select(group => new FacilitySnapshotDto
                    {
                        DescriptorId = group.Key,
                        Quantity = group.Sum(x => Math.Max(0L, x.Quantity)),
                        Enabled = group.Sum(x => Math.Max(0L, x.Enabled)),
                        HaveWorkers = group.Sum(x => Math.Max(0L, x.HaveWorkers)),
                        ValidCanAddFacility = group.Any(x => x.ValidCanAddFacility)
                    })
                    .OrderBy(x => x.DescriptorId)
                    .ToList() ?? new List<FacilitySnapshotDto>()
            };

            if (inventory.Facilities.Count > 0)
            {
                snapshot.OwnedInventories.Add(inventory);
            }
        }

        snapshot.OwnedInventories = snapshot.OwnedInventories
            .OrderBy(x => x.ObjectId)
            .ToList();
    }

    private void ApplyInventoryState(GameCompany company, CompanyStateSnapshotMessage snapshot)
    {
        var objectInfoManager = UnityEngine.Object.FindObjectOfType<ObjectInfoManager>();
        var scriptableObjectManager = UnityEngine.Object.FindObjectOfType<AllScriptableObjectManager>();
        if (objectInfoManager == null || scriptableObjectManager?.AllFacility == null)
        {
            return;
        }

        var includedObjectIds = new HashSet<int>(snapshot.OwnedInventories.Select(x => x.ObjectId));
        foreach (var inventory in snapshot.OwnedInventories)
        {
            var objectInfo = objectInfoManager.GetByID(inventory.ObjectId);
            if (objectInfo == null)
            {
                continue;
            }

            var objectInfoData = objectInfo.GetObjectInfoData(company);
            if (objectInfoData == null)
            {
                continue;
            }

            ApplyFacilities(objectInfoData, inventory, scriptableObjectManager);
            objectInfoData.MarkIsDirty();
            objectInfoData.InvokeResourcesChange2();
            objectInfoData.InvokeRefreshUIAddFacilityOrBuildProductItem();
        }

        foreach (var objectInfo in UnityEngine.Object.FindObjectsOfType<GameObjectInfo>())
        {
            if (includedObjectIds.Contains(objectInfo.id))
            {
                continue;
            }

            var objectInfoData = objectInfo.ObjectsInfoData?.FirstOrDefault(x => x != null && x.company == company);
            if (objectInfoData?.ListFacility == null || objectInfoData.ListFacility.Count == 0)
            {
                continue;
            }

            ApplyFacilities(objectInfoData, new ObjectInventorySnapshotDto { ObjectId = objectInfo.id }, scriptableObjectManager);
            objectInfoData.MarkIsDirty();
            objectInfoData.InvokeRefreshUIAddFacilityOrBuildProductItem();
        }
    }

    private static void ApplyFacilities(ObjectInfoDataType objectInfoData, ObjectInventorySnapshotDto inventory, AllScriptableObjectManager scriptableObjectManager)
    {
        var facilities = objectInfoData.ListFacility;
        if (facilities == null)
        {
            return;
        }

        var desired = inventory.Facilities
            .Where(x => !string.IsNullOrWhiteSpace(x.DescriptorId) && x.Quantity > 0)
            .GroupBy(x => x.DescriptorId)
            .ToDictionary(
                group => group.Key,
                group => new FacilitySnapshotDto
                {
                    DescriptorId = group.Key,
                    Quantity = group.Sum(x => Math.Max(0L, x.Quantity)),
                    Enabled = group.Sum(x => Math.Max(0L, x.Enabled)),
                    HaveWorkers = group.Sum(x => Math.Max(0L, x.HaveWorkers)),
                    ValidCanAddFacility = group.Any(x => x.ValidCanAddFacility)
                });

        foreach (var facility in facilities.ToArray())
        {
            var descriptorId = facility?.facilityDescriptor?.ID;
            if (facility == null || string.IsNullOrWhiteSpace(descriptorId) || !facility.FinishConstructionBool || !desired.ContainsKey(descriptorId!))
            {
                RemoveFacilityWithoutRefund(objectInfoData, facility);
            }
        }

        foreach (var facilitySnapshot in desired.Values.OrderBy(x => x.DescriptorId))
        {
            var descriptor = scriptableObjectManager.AllFacility?.GetByID(facilitySnapshot.DescriptorId);
            if (descriptor == null)
            {
                continue;
            }

            var current = facilities.FirstOrDefault(x => x != null && x.facilityDescriptor == descriptor && x.FinishConstructionBool);
            var currentQuantity = current?.Quantity ?? 0L;
            var desiredQuantity = Math.Max(0L, facilitySnapshot.Quantity);

            if (currentQuantity < desiredQuantity)
            {
                var missing = Math.Min(desiredQuantity - currentQuantity, 10000L);
                for (var i = 0L; i < missing; i++)
                {
                    objectInfoData.AddFacility(descriptor, prebuilt: true);
                }
            }
            else if (current != null && currentQuantity > desiredQuantity)
            {
                current.Scrap(currentQuantity - desiredQuantity, addResourceOnScrap: false);
            }

            current = facilities.FirstOrDefault(x => x != null && x.facilityDescriptor == descriptor && x.FinishConstructionBool);
            if (current == null)
            {
                continue;
            }

            current.Enabled = Math.Min(Math.Max(0L, facilitySnapshot.Enabled), current.Quantity);
            current.HaveWorkers = Math.Min(Math.Max(0L, facilitySnapshot.HaveWorkers), current.Quantity);
            current.ValidCanAddFacility = facilitySnapshot.ValidCanAddFacility;
        }

        objectInfoData.UpdateFacilityRelatedSummaries(resetHabitability: true);
    }

    private static void RemoveFacilityWithoutRefund(ObjectInfoDataType objectInfoData, FacilityType? facility)
    {
        if (facility == null)
        {
            return;
        }

        if (facility.Quantity > 0)
        {
            facility.Scrap(facility.Quantity, addResourceOnScrap: false);
            return;
        }

        objectInfoData.RemoveProductionItem(facility);
    }

    private static void ClearRemoteCompanyPrivateState(GameCompany company)
    {
        var objectInfos = UnityEngine.Object.FindObjectsOfType<GameObjectInfo>();
        foreach (var objectInfo in objectInfos)
        {
            var objectInfoData = objectInfo.ObjectsInfoData?.FirstOrDefault(x => x != null && x.company == company);
            if (objectInfoData == null)
            {
                continue;
            }

            objectInfoData.ListRowResourcesData = new List<RowResourcesDataType>();
            objectInfoData.ConstructionEquipmentCount = 0;
            objectInfoData.MarkIsDirty();
            objectInfoData.InvokeResourcesChange2();
        }
    }

}
