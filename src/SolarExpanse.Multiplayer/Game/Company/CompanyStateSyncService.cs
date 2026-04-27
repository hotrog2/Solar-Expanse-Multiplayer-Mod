using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    private readonly Dictionary<int, CompanySnapshotDiagnostics> _snapshotDiagnostics = new Dictionary<int, CompanySnapshotDiagnostics>();
    private readonly Dictionary<int, long> _nextSequences = new Dictionary<int, long>();
    private readonly Dictionary<int, Dictionary<int, string>> _lastSentObjectFingerprints = new Dictionary<int, Dictionary<int, string>>();
    private readonly Dictionary<int, string> _lastAppliedFingerprints = new Dictionary<int, string>();

    private float _nextSendAt;
    private string? _lastSentPublicFingerprint;
    private bool _hasUnappliedSnapshots;

    public CompanyStateSyncService(CompanyOwnershipService companyOwnershipService)
    {
        _companyOwnershipService = companyOwnershipService;
    }

    public IReadOnlyDictionary<int, CompanyStateSnapshotMessage> LatestSnapshots => _latestSnapshots;
    public IReadOnlyDictionary<int, CompanySnapshotDiagnostics> SnapshotDiagnostics => _snapshotDiagnostics;

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

        var publicSnapshot = CreateOutgoingPublicSnapshot(snapshot, hostAuthoritative: true, fullSnapshot: false);
        HandleIncomingSnapshot(publicSnapshot);
        ApplyRemoteSnapshots(localCompanySlot);
        host.Broadcast(publicSnapshot);
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

        var publicSnapshot = CreateOutgoingPublicSnapshot(snapshot, hostAuthoritative: false, fullSnapshot: false);
        HandleIncomingSnapshot(publicSnapshot);
        client.Send(publicSnapshot);
    }

    public void HandleIncomingSnapshot(CompanyStateSnapshotMessage snapshot)
    {
        snapshot = MergeIncomingSnapshot(snapshot);
        var diagnostics = GetDiagnostics(snapshot.CompanySlot);
        var receivedTicks = DateTime.UtcNow.Ticks;
        if (snapshot.SentUtcTicks <= 0)
        {
            snapshot.SentUtcTicks = receivedTicks;
        }

        var incomingFingerprint = snapshot.SnapshotFingerprint;
        StampObjectFingerprints(snapshot);
        var computedFingerprint = ComputeSnapshotFingerprint(snapshot);
        if (string.IsNullOrWhiteSpace(incomingFingerprint))
        {
            snapshot.SnapshotFingerprint = computedFingerprint;
        }
        else if (!string.Equals(incomingFingerprint, computedFingerprint, StringComparison.Ordinal))
        {
            diagnostics.ChecksumMismatches++;
            diagnostics.NeedsResync = true;
            diagnostics.LastReason = "Snapshot fingerprint mismatch.";
            snapshot.SnapshotFingerprint = computedFingerprint;
        }

        if (!snapshot.FullSnapshot && snapshot.Sequence > 0 && diagnostics.LastSequence > 0)
        {
            if (snapshot.Sequence <= diagnostics.LastSequence)
            {
                diagnostics.StaleSnapshots++;
                diagnostics.LastReceivedUtcTicks = receivedTicks;
                diagnostics.LastReason = $"Ignored stale sequence {snapshot.Sequence}.";
                return;
            }

            if (snapshot.Sequence > diagnostics.LastSequence + 1)
            {
                diagnostics.MissedSnapshots += snapshot.Sequence - diagnostics.LastSequence - 1;
                diagnostics.NeedsResync = true;
                diagnostics.LastReason = $"Missed sequence {diagnostics.LastSequence + 1}.";
            }
        }

        if (snapshot.FullSnapshot)
        {
            diagnostics.NeedsResync = false;
            diagnostics.LastReason = "Full snapshot received.";
        }

        _latestSnapshots[snapshot.CompanySlot] = snapshot;
        _hasUnappliedSnapshots = true;
        diagnostics.CompanySlot = snapshot.CompanySlot;
        diagnostics.LastSequence = Math.Max(diagnostics.LastSequence, snapshot.Sequence);
        diagnostics.LastReceivedUtcTicks = receivedTicks;
        diagnostics.LastSnapshotSentUtcTicks = snapshot.SentUtcTicks;
        diagnostics.LastFingerprint = snapshot.SnapshotFingerprint;
        diagnostics.LastObjectCount = snapshot.OwnedInventories.Count;
        diagnostics.LastFacilityTypeCount = snapshot.OwnedInventories.Sum(inventory => inventory.Facilities.Count);
        diagnostics.HostAuthoritative = snapshot.HostAuthoritative;
        diagnostics.LastSnapshotWasFull = snapshot.FullSnapshot;
    }

    public CompanyStateSnapshotMessage? CaptureForcedLocalSnapshot(int localCompanySlot, string localPlayerName)
    {
        return CaptureLocalSnapshot(localCompanySlot, localPlayerName, force: true);
    }

    public CompanyStateSnapshotMessage? CaptureForcedPublicSnapshot(int localCompanySlot, string localPlayerName, bool hostAuthoritative, bool fullSnapshot)
    {
        var snapshot = CaptureLocalSnapshot(localCompanySlot, localPlayerName, force: true);
        return snapshot == null
            ? null
            : CreateOutgoingPublicSnapshot(snapshot, hostAuthoritative, fullSnapshot);
    }

    public CompanyStateSnapshotMessage CreateOutgoingPublicSnapshot(CompanyStateSnapshotMessage snapshot, bool hostAuthoritative, bool fullSnapshot)
    {
        var publicSnapshot = CreatePublicSnapshot(snapshot);
        publicSnapshot.HostAuthoritative = hostAuthoritative;
        publicSnapshot.FullSnapshot = fullSnapshot;
        publicSnapshot.SentUtcTicks = DateTime.UtcNow.Ticks;
        StampFingerprints(publicSnapshot);
        if (!fullSnapshot)
        {
            ApplyDirtyInventoryFilter(publicSnapshot);
        }
        else
        {
            RememberSentObjectFingerprints(publicSnapshot);
        }

        publicSnapshot.Sequence = NextSequence(publicSnapshot.CompanySlot);
        return publicSnapshot;
    }

    public void MarkResyncRequested(int companySlot, string reason)
    {
        var diagnostics = GetDiagnostics(companySlot);
        diagnostics.NeedsResync = false;
        diagnostics.LastResyncRequestUtcTicks = DateTime.UtcNow.Ticks;
        diagnostics.LastReason = reason;
    }

    public IReadOnlyList<int> GetSlotsNeedingResync(int localCompanySlot)
    {
        return _snapshotDiagnostics.Values
            .Where(x => x.CompanySlot != localCompanySlot && x.NeedsResync)
            .OrderBy(x => x.CompanySlot)
            .Select(x => x.CompanySlot)
            .ToArray();
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
            Money = snapshot.Money,
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
        if (!_hasUnappliedSnapshots)
        {
            return;
        }

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

            var fingerprint = ComputeApplyFingerprint(snapshot);
            if (!snapshot.FullSnapshot &&
                _lastAppliedFingerprints.TryGetValue(snapshot.CompanySlot, out var lastFingerprint) &&
                string.Equals(lastFingerprint, fingerprint, StringComparison.Ordinal))
            {
                continue;
            }

            ClearRemoteCompanyPrivateState(company);
            ApplyInventoryState(company, snapshot);
            if (_snapshotDiagnostics.TryGetValue(snapshot.CompanySlot, out var diagnostics))
            {
                diagnostics.LastAppliedUtcTicks = DateTime.UtcNow.Ticks;
            }

            _lastAppliedFingerprints[snapshot.CompanySlot] = fingerprint;
        }

        _hasUnappliedSnapshots = false;
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

    private long NextSequence(int companySlot)
    {
        if (!_nextSequences.TryGetValue(companySlot, out var sequence))
        {
            sequence = 0;
        }

        if (_snapshotDiagnostics.TryGetValue(companySlot, out var diagnostics))
        {
            sequence = Math.Max(sequence, diagnostics.LastSequence);
        }

        sequence++;
        _nextSequences[companySlot] = sequence;
        return sequence;
    }

    private CompanySnapshotDiagnostics GetDiagnostics(int companySlot)
    {
        if (!_snapshotDiagnostics.TryGetValue(companySlot, out var diagnostics))
        {
            diagnostics = new CompanySnapshotDiagnostics { CompanySlot = companySlot };
            _snapshotDiagnostics[companySlot] = diagnostics;
        }

        return diagnostics;
    }

    private CompanyStateSnapshotMessage MergeIncomingSnapshot(CompanyStateSnapshotMessage incoming)
    {
        if (incoming.FullSnapshot || !_latestSnapshots.TryGetValue(incoming.CompanySlot, out var previous))
        {
            return incoming;
        }

        var mergedInventories = previous.OwnedInventories
            .Where(x => x != null)
            .ToDictionary(x => x.ObjectId, x => CloneInventory(x));

        foreach (var inventory in incoming.OwnedInventories.Where(x => x != null))
        {
            if (inventory.Facilities.Count == 0)
            {
                mergedInventories.Remove(inventory.ObjectId);
                continue;
            }

            mergedInventories[inventory.ObjectId] = CloneInventory(inventory);
        }

        return new CompanyStateSnapshotMessage
        {
            MessageType = nameof(CompanyStateSnapshotMessage),
            Sequence = incoming.Sequence,
            SentUtcTicks = incoming.SentUtcTicks,
            FullSnapshot = false,
            HostAuthoritative = incoming.HostAuthoritative,
            SnapshotFingerprint = incoming.SnapshotFingerprint,
            CompanySlot = incoming.CompanySlot,
            CompanyId = incoming.CompanyId,
            CompanyName = incoming.CompanyName,
            OwnerPlayerName = incoming.OwnerPlayerName,
            Money = incoming.Money,
            TotalProfit = incoming.TotalProfit,
            DiscoveredSystemsCount = incoming.DiscoveredSystemsCount,
            CompletedResearchCount = incoming.CompletedResearchCount,
            CompletedResearchIds = incoming.CompletedResearchIds ?? new List<string>(),
            ActiveResearch = incoming.ActiveResearch ?? new List<ResearchProgressDto>(),
            OwnedInventories = mergedInventories.Values.OrderBy(x => x.ObjectId).ToList()
        };
    }

    private static ObjectInventorySnapshotDto CloneInventory(ObjectInventorySnapshotDto inventory)
    {
        return new ObjectInventorySnapshotDto
        {
            ObjectId = inventory.ObjectId,
            ObjectName = inventory.ObjectName,
            ObjectFingerprint = inventory.ObjectFingerprint,
            ConstructionEquipmentCount = inventory.ConstructionEquipmentCount,
            Resources = inventory.Resources?.Select(x => new ResourceStackDto
            {
                ResourceId = x.ResourceId,
                Value = x.Value,
                ResourceState = x.ResourceState,
                ForcePrimary = x.ForcePrimary,
                MiningFactor = x.MiningFactor
            }).ToList() ?? new List<ResourceStackDto>(),
            Facilities = inventory.Facilities?.Select(x => new FacilitySnapshotDto
            {
                DescriptorId = x.DescriptorId,
                Quantity = x.Quantity,
                Enabled = x.Enabled,
                HaveWorkers = x.HaveWorkers,
                ValidCanAddFacility = x.ValidCanAddFacility
            }).ToList() ?? new List<FacilitySnapshotDto>()
        };
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

        var publicSnapshot = CreatePublicSnapshot(snapshot);
        StampFingerprints(publicSnapshot);
        var fingerprint = publicSnapshot.SnapshotFingerprint;
        if (!force && string.Equals(_lastSentPublicFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        _lastSentPublicFingerprint = fingerprint;
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

    private static void StampFingerprints(CompanyStateSnapshotMessage snapshot)
    {
        StampObjectFingerprints(snapshot);
        snapshot.SnapshotFingerprint = ComputeSnapshotFingerprint(snapshot);
    }

    private void ApplyDirtyInventoryFilter(CompanyStateSnapshotMessage snapshot)
    {
        var current = snapshot.OwnedInventories
            .Where(x => x != null)
            .ToDictionary(x => x.ObjectId, x => x.ObjectFingerprint);

        if (!_lastSentObjectFingerprints.TryGetValue(snapshot.CompanySlot, out var previous))
        {
            previous = new Dictionary<int, string>();
            _lastSentObjectFingerprints[snapshot.CompanySlot] = previous;
        }

        var changedObjectIds = new HashSet<int>();
        foreach (var pair in current)
        {
            if (!previous.TryGetValue(pair.Key, out var lastFingerprint) ||
                !string.Equals(lastFingerprint, pair.Value, StringComparison.Ordinal))
            {
                changedObjectIds.Add(pair.Key);
            }
        }

        var removedObjectIds = previous.Keys
            .Where(objectId => !current.ContainsKey(objectId))
            .ToArray();

        snapshot.OwnedInventories = snapshot.OwnedInventories
            .Where(x => changedObjectIds.Contains(x.ObjectId))
            .Select(CloneInventory)
            .ToList();

        foreach (var removedObjectId in removedObjectIds)
        {
            var tombstone = new ObjectInventorySnapshotDto
            {
                ObjectId = removedObjectId,
                ObjectName = $"Object {removedObjectId}",
                Facilities = new List<FacilitySnapshotDto>()
            };
            tombstone.ObjectFingerprint = ComputeInventoryFingerprint(tombstone);
            snapshot.OwnedInventories.Add(tombstone);
        }

        RememberSentObjectFingerprints(snapshot.CompanySlot, current);
    }

    private void RememberSentObjectFingerprints(CompanyStateSnapshotMessage snapshot)
    {
        RememberSentObjectFingerprints(
            snapshot.CompanySlot,
            snapshot.OwnedInventories
                .Where(x => x != null)
                .ToDictionary(x => x.ObjectId, x => x.ObjectFingerprint));
    }

    private void RememberSentObjectFingerprints(int companySlot, Dictionary<int, string> current)
    {
        _lastSentObjectFingerprints[companySlot] = new Dictionary<int, string>(current);
    }

    private static void StampObjectFingerprints(CompanyStateSnapshotMessage snapshot)
    {
        foreach (var inventory in snapshot.OwnedInventories)
        {
            inventory.ObjectFingerprint = ComputeInventoryFingerprint(inventory);
        }
    }

    private static string ComputeSnapshotFingerprint(CompanyStateSnapshotMessage snapshot)
    {
        StampObjectFingerprints(snapshot);
        var canonical = new
        {
            snapshot.CompanySlot,
            snapshot.CompanyId,
            snapshot.CompanyName,
            snapshot.OwnerPlayerName,
            Money = Math.Round(snapshot.Money, 2),
            Objects = snapshot.OwnedInventories
                .OrderBy(x => x.ObjectId)
                .Select(x => new
                {
                    x.ObjectId,
                    x.ObjectName,
                    x.ObjectFingerprint
                })
                .ToArray()
        };

        return Sha256(JsonConvert.SerializeObject(canonical));
    }

    private static string ComputeApplyFingerprint(CompanyStateSnapshotMessage snapshot)
    {
        StampObjectFingerprints(snapshot);
        var canonical = new
        {
            snapshot.CompanySlot,
            Objects = snapshot.OwnedInventories
                .OrderBy(x => x.ObjectId)
                .Select(x => new
                {
                    x.ObjectId,
                    x.ObjectFingerprint
                })
                .ToArray()
        };

        return Sha256(JsonConvert.SerializeObject(canonical));
    }

    private static string ComputeInventoryFingerprint(ObjectInventorySnapshotDto inventory)
    {
        var canonical = new
        {
            inventory.ObjectId,
            inventory.ObjectName,
            Facilities = inventory.Facilities
                .OrderBy(x => x.DescriptorId)
                .Select(x => new
                {
                    x.DescriptorId,
                    x.Quantity,
                    x.Enabled,
                    x.HaveWorkers,
                    x.ValidCanAddFacility
                })
                .ToArray()
        };

        return Sha256(JsonConvert.SerializeObject(canonical));
    }

    private static string Sha256(string value)
    {
        using (var sha = SHA256.Create())
        {
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}

public sealed class CompanySnapshotDiagnostics
{
    public int CompanySlot { get; set; }
    public long LastSequence { get; set; }
    public long LastReceivedUtcTicks { get; set; }
    public long LastAppliedUtcTicks { get; set; }
    public long LastSnapshotSentUtcTicks { get; set; }
    public long LastResyncRequestUtcTicks { get; set; }
    public string LastFingerprint { get; set; } = string.Empty;
    public long MissedSnapshots { get; set; }
    public long StaleSnapshots { get; set; }
    public long ChecksumMismatches { get; set; }
    public bool NeedsResync { get; set; }
    public bool HostAuthoritative { get; set; }
    public bool LastSnapshotWasFull { get; set; }
    public int LastObjectCount { get; set; }
    public int LastFacilityTypeCount { get; set; }
    public string LastReason { get; set; } = string.Empty;

    public double AgeSeconds
    {
        get
        {
            if (LastReceivedUtcTicks <= 0)
            {
                return double.PositiveInfinity;
            }

            return Math.Max(0, (DateTime.UtcNow.Ticks - LastReceivedUtcTicks) / (double)TimeSpan.TicksPerSecond);
        }
    }
}
