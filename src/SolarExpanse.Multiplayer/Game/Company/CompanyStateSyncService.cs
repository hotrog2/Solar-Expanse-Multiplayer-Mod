using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
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
using MoneyControllerType = global::Game.CompanyScripts.MoneyController;

namespace SolarExpanse.Multiplayer.Game.Company;

public sealed class CompanyStateSyncService
{
    private readonly AccessTools.FieldRef<MoneyControllerType, double> _totalProfitRef = AccessTools.FieldRefAccess<MoneyControllerType, double>("totalProfit");
    private readonly CompanyOwnershipService _companyOwnershipService;
    private readonly Dictionary<int, CompanyStateSnapshotMessage> _latestSnapshots = new Dictionary<int, CompanyStateSnapshotMessage>();
    private readonly Dictionary<int, string> _lastAppliedFingerprints = new Dictionary<int, string>();

    private float _nextSendAt;
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
        client.Send(snapshot);
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
        foreach (var snapshot in _latestSnapshots.Values.OrderBy(x => x.CompanySlot))
        {
            if (snapshot.CompanySlot == localCompanySlot)
            {
                continue;
            }

            var fingerprint = JsonConvert.SerializeObject(snapshot);
            if (_lastAppliedFingerprints.TryGetValue(snapshot.CompanySlot, out var lastFingerprint) &&
                string.Equals(lastFingerprint, fingerprint, StringComparison.Ordinal))
            {
                continue;
            }

            if (!_companyOwnershipService.TryGetCompanyBySlot(snapshot.CompanySlot, out var company) || company == null)
            {
                continue;
            }

            ApplyMoneyState(company, snapshot);
            ApplyResearchState(company, snapshot);
            ApplyInventoryState(company, snapshot);
            _lastAppliedFingerprints[snapshot.CompanySlot] = fingerprint;
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
            CompanyName = company.name ?? company.ID ?? $"Company {localCompanySlot}",
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

    private void ApplyMoneyState(GameCompany company, CompanyStateSnapshotMessage snapshot)
    {
        if (company.MoneyController == null)
        {
            return;
        }

        company.MoneyController.ForCheatSetCurrentMony(snapshot.Money);
        _totalProfitRef(company.MoneyController) = snapshot.TotalProfit;
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
                ConstructionEquipmentCount = objectInfoData.ConstructionEquipmentCount,
                Resources = objectInfoData.ListRowResourcesData?
                    .Where(x => x != null && x.ResourcesType != null)
                    .Select(x => new ResourceStackDto
                    {
                        ResourceId = x.ResourcesType.ID,
                        Value = x.Value,
                        ResourceState = (int)x.ResourceState,
                        ForcePrimary = x.ForcePrimary,
                        MiningFactor = x.MiningFactor
                    })
                    .OrderBy(x => x.ResourceId)
                    .ToList() ?? new List<ResourceStackDto>(),
                Facilities = objectInfoData.ListFacility?
                    .Where(x => x != null && x.facilityDescriptor != null)
                    .Select(x => new FacilitySnapshotDto
                    {
                        DescriptorId = x.facilityDescriptor.ID,
                        Quantity = x.Quantity,
                        Enabled = x.Enabled,
                        HaveWorkers = x.HaveWorkers,
                        ValidCanAddFacility = x.ValidCanAddFacility
                    })
                    .OrderBy(x => x.DescriptorId)
                    .ToList() ?? new List<FacilitySnapshotDto>()
            };

            if (inventory.ConstructionEquipmentCount > 0 || inventory.Resources.Count > 0 || inventory.Facilities.Count > 0)
            {
                snapshot.OwnedInventories.Add(inventory);
            }
        }

        snapshot.OwnedInventories = snapshot.OwnedInventories
            .OrderBy(x => x.ObjectId)
            .ToList();
    }

    private static void ApplyResearchState(GameCompany company, CompanyStateSnapshotMessage snapshot)
    {
        if (company.ResearchManager == null)
        {
            return;
        }

        var data = new ResearchDataToSave
        {
            completeResearch = snapshot.CompletedResearchIds
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .OrderBy(x => x)
                .Select(x => new ResearchDefinitionSave { id = x })
                .ToList(),
            startNotFinish = new List<ResearchDataProgress>(),
            dictionaryCandidateResearchList = new List<ResearchTypeKeyToListResearchDefinitionSave>(),
            queueRD = new Queue<ResearchDefinition>(),
            bonusFromObservatory = 0f
        };

        data.slot1 = null;
        data.slot2 = null;
        data.slot3 = null;

        company.ResearchManager.SetDataFromSave(data);
    }

    private void ApplyInventoryState(GameCompany company, CompanyStateSnapshotMessage snapshot)
    {
        var objectInfoManager = UnityEngine.Object.FindObjectOfType<ObjectInfoManager>();
        var scriptableObjectManager = UnityEngine.Object.FindObjectOfType<AllScriptableObjectManager>();
        if (objectInfoManager == null || scriptableObjectManager?.AllResourceDefinitions == null)
        {
            return;
        }

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

            var rows = new List<RowResourcesDataType>();
            foreach (var resource in inventory.Resources)
            {
                var resourceDefinition = scriptableObjectManager.AllResourceDefinitions.GetByID(resource.ResourceId);
                if (resourceDefinition == null)
                {
                    continue;
                }

                var row = new RowResourcesDataType
                {
                    ResourcesType = resourceDefinition,
                    ObjectInfoData = objectInfoData,
                    Value = resource.Value,
                    ResourceState = (RowResourcesDataType.EResourceState)resource.ResourceState,
                    ForcePrimary = resource.ForcePrimary,
                    MiningFactor = resource.MiningFactor
                };

                rows.Add(row);
            }

            objectInfoData.ListRowResourcesData = rows;
            objectInfoData.ConstructionEquipmentCount = inventory.ConstructionEquipmentCount;
            ApplyFacilities(objectInfoData, inventory, scriptableObjectManager);
            objectInfoData.MarkIsDirty();
            objectInfoData.InvokeResourcesChange2();
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

        facilities.Clear();
        foreach (var facilitySnapshot in inventory.Facilities)
        {
            if (string.IsNullOrWhiteSpace(facilitySnapshot.DescriptorId))
            {
                continue;
            }

            var descriptor = scriptableObjectManager.AllFacility?.GetByID(facilitySnapshot.DescriptorId);
            if (descriptor == null)
            {
                continue;
            }

            var quantity = facilitySnapshot.Quantity < 0 ? 0 : facilitySnapshot.Quantity;
            var facility = new FacilityType(descriptor, objectInfoData, quantity > int.MaxValue ? int.MaxValue : (int)quantity)
            {
                Enabled = facilitySnapshot.Enabled,
                HaveWorkers = facilitySnapshot.HaveWorkers,
                ValidCanAddFacility = facilitySnapshot.ValidCanAddFacility
            };

            facilities.Add(facility);
        }
    }

}
