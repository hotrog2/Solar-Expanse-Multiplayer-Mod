using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using Manager;
using SolarExpanse.Multiplayer.Game.Company;
using SolarExpanse.Multiplayer.Networking.Protocol;
using ScriptableObjectScripts;
using UnityEngine;

using GameCompany = global::Game.Company;
using GameObjectInfo = global::Game.Info.ObjectInfo;
using MarketOfferManagerType = global::Manager.MarketOfferManager;
using OfferDataType = global::Game.ObjectInfoDataScripts.OfferData;
using OfferType = global::Game.ObjectInfoDataScripts.Offer;

namespace SolarExpanse.Multiplayer.Game.Trade;

public sealed class TradeSyncService
{
    private static readonly object SuppressionLock = new object();

    private static readonly AccessTools.FieldRef<OfferType, double> CountCompletedRef =
        AccessTools.FieldRefAccess<OfferType, double>("countCompleted");

    private static readonly AccessTools.FieldRef<OfferType, bool> OfferDoneRef =
        AccessTools.FieldRefAccess<OfferType, bool>("offerDone");

    private readonly CompanyOwnershipService _companyOwnershipService;
    private readonly ManualLogSource _log;
    private readonly HashSet<Guid> _seenFulfillRequests = new HashSet<Guid>();

    public TradeSyncService(CompanyOwnershipService companyOwnershipService, ManualLogSource log)
    {
        _companyOwnershipService = companyOwnershipService;
        _log = log;
    }

    public static bool SuppressLocalHooks { get; private set; }

    public static void RunWithSuppressedHooks(Action action)
    {
        lock (SuppressionLock)
        {
            SuppressLocalHooks = true;
            try
            {
                action();
            }
            finally
            {
                SuppressLocalHooks = false;
            }
        }
    }

    public TradeOfferSyncMessage? CreateOfferSync(OfferType? offer, string operation, string ownerPlayerName, string ownerCompanyName, int expectedOwnerSlot)
    {
        if (offer == null || !_companyOwnershipService.TryGetSlotForCompany(offer.Company, out var ownerSlot))
        {
            return null;
        }

        if (ownerSlot != expectedOwnerSlot)
        {
            return null;
        }

        return new TradeOfferSyncMessage
        {
            MessageType = nameof(TradeOfferSyncMessage),
            SyncId = Guid.NewGuid(),
            Operation = operation,
            OfferId = offer.ID,
            OwnerCompanySlot = ownerSlot,
            OwnerPlayerName = ownerPlayerName,
            OwnerCompanyName = ownerCompanyName,
            ObjectId = offer.WhereOffer?.id ?? -1,
            ObjectName = offer.WhereOffer?.ObjectName ?? string.Empty,
            ResourceId = offer.Rd?.ID ?? string.Empty,
            BuySell = offer.BuySell,
            PricePerUnit = offer.PricePerUnit,
            CountToBuySell = offer.CountToBuySell,
            CountCompleted = offer.CountCompleted,
            OfferDone = offer.OfferDone,
            OfferStartUtcTicks = offer.OfferStartDate.Ticks,
            SentUtcTicks = DateTime.UtcNow.Ticks
        };
    }

    public TradeOfferFulfillMessage? CreateFulfillMessage(OfferType? offer, GameCompany? takerCompany, double count, string takerPlayerName, string takerCompanyName, Guid? requestId = null)
    {
        if (offer == null ||
            !_companyOwnershipService.TryGetSlotForCompany(offer.Company, out var ownerSlot) ||
            !_companyOwnershipService.TryGetSlotForCompany(takerCompany, out var takerSlot))
        {
            return null;
        }

        if (ownerSlot == takerSlot)
        {
            return null;
        }

        return new TradeOfferFulfillMessage
        {
            MessageType = nameof(TradeOfferFulfillMessage),
            RequestId = requestId ?? Guid.NewGuid(),
            OfferId = offer.ID,
            OwnerCompanySlot = ownerSlot,
            TakerCompanySlot = takerSlot,
            TakerPlayerName = takerPlayerName,
            TakerCompanyName = takerCompanyName,
            Count = Math.Max(0, count),
            SentUtcTicks = DateTime.UtcNow.Ticks
        };
    }

    public void ApplyOfferSync(TradeOfferSyncMessage message)
    {
        if (message.OwnerCompanySlot == _companyOwnershipService.LocalCompanySlot)
        {
            return;
        }

        RunWithSuppressedHooks(() =>
        {
            if (string.Equals(message.Operation, "Cancel", StringComparison.OrdinalIgnoreCase) ||
                message.OfferDone)
            {
                RemoveRemoteOffer(message.OwnerCompanySlot, message.OfferId);
                return;
            }

            UpsertRemoteOffer(message);
        });
    }

    public void ApplyFulfillment(TradeOfferFulfillMessage message)
    {
        if (message.Count <= 0 || !_seenFulfillRequests.Add(message.RequestId))
        {
            return;
        }

        RunWithSuppressedHooks(() =>
        {
            if (message.OwnerCompanySlot == _companyOwnershipService.LocalCompanySlot)
            {
                ApplyOwnerSideFulfillment(message);
                return;
            }

            if (message.TakerCompanySlot == _companyOwnershipService.LocalCompanySlot)
            {
                return;
            }

            ApplyObserverFulfillment(message);
        });
    }

    public IReadOnlyList<TradeOfferSyncMessage> CreateFullOfferSync(string localPlayerName, string localCompanyName)
    {
        var manager = FindMarketOfferManager();
        if (manager == null)
        {
            return Array.Empty<TradeOfferSyncMessage>();
        }

        return manager.Offerts
            .Where(offer => offer != null && !offer.OfferDone)
            .Select(offer =>
            {
                var ownerPlayerName = localPlayerName;
                var ownerCompanyName = localCompanyName;
                if (_companyOwnershipService.TryGetSlotForCompany(offer.Company, out var slot) &&
                    _companyOwnershipService.TryGetCompanyDisplayName(slot, out var displayName))
                {
                    ownerCompanyName = displayName;
                }

                return CreateOfferSync(offer, "Upsert", ownerPlayerName, ownerCompanyName, _companyOwnershipService.TryGetSlotForCompany(offer.Company, out var ownerSlot) ? ownerSlot : -1);
            })
            .Where(message => message != null)
            .Cast<TradeOfferSyncMessage>()
            .ToArray();
    }

    private void UpsertRemoteOffer(TradeOfferSyncMessage message)
    {
        var manager = FindMarketOfferManager();
        var objectInfoManager = UnityEngine.Object.FindObjectOfType<ObjectInfoManager>();
        var scriptableObjectManager = UnityEngine.Object.FindObjectOfType<AllScriptableObjectManager>();
        if (manager == null ||
            objectInfoManager == null ||
            scriptableObjectManager?.AllResourceDefinitions == null ||
            !_companyOwnershipService.TryGetCompanyBySlot(message.OwnerCompanySlot, out var ownerCompany) ||
            ownerCompany == null)
        {
            return;
        }

        var objectInfo = objectInfoManager.GetByID(message.ObjectId);
        var resource = scriptableObjectManager.AllResourceDefinitions.GetByID(message.ResourceId);
        if (objectInfo == null || resource == null)
        {
            return;
        }

        var existing = FindOffer(manager, message.OwnerCompanySlot, message.OfferId);
        if (existing != null)
        {
            SetOfferProgress(existing, message.CountCompleted, message.OfferDone);
            InvokeOfferUiRefresh(manager);
            return;
        }

        var offerData = new OfferDataType
        {
            whereOffer = objectInfo,
            offerStartDate = message.OfferStartUtcTicks > 0 ? new DateTime(message.OfferStartUtcTicks) : DateTime.UtcNow,
            buySell = message.BuySell,
            pricePerUnit = message.PricePerUnit,
            rd = resource,
            countToBuySell = message.CountToBuySell,
            company = ownerCompany,
            canDoSubOffer = true
        };

        var offer = new OfferType(offerData);
        offer.SetID(message.OfferId);
        offer.offerCountWhenAdd = message.OfferId;
        SetOfferProgress(offer, message.CountCompleted, message.OfferDone);

        manager.Offerts.Add(offer);
        SubscribeOfferPartComplete(manager, offer);
        InvokeOfferUiRefresh(manager);
    }

    private void ApplyOwnerSideFulfillment(TradeOfferFulfillMessage message)
    {
        var manager = FindMarketOfferManager();
        var offer = FindOffer(manager, message.OwnerCompanySlot, message.OfferId);
        if (manager == null || offer == null)
        {
            _log.LogWarning($"Could not apply trade fulfillment for offer {message.OfferId}; offer not found.");
            return;
        }

        var count = Math.Min(message.Count, offer.CountLeft);
        if (count <= 0)
        {
            return;
        }

        if (offer.BuySell)
        {
            offer.WhereOffer.GetObjectInfoData(offer.Company).AddResources(offer.Rd, count);
        }
        else
        {
            offer.Company.MoneyController.AddMoney(offer.PricePerUnit * count, registerProfit: true);
        }

        manager.InvokeOnOfferPartFullFill(offer, count);
        SetOfferProgress(offer, offer.CountCompleted + count, offer.CountCompleted + count >= offer.CountToBuySell);
        if (offer.OfferDone)
        {
            manager.InvokeOnOfferFullfill(offer);
        }

        manager.RefreshForUI();
    }

    private void ApplyObserverFulfillment(TradeOfferFulfillMessage message)
    {
        var manager = FindMarketOfferManager();
        var offer = FindOffer(manager, message.OwnerCompanySlot, message.OfferId);
        if (manager == null || offer == null)
        {
            return;
        }

        var countCompleted = Math.Min(offer.CountToBuySell, offer.CountCompleted + message.Count);
        SetOfferProgress(offer, countCompleted, countCompleted >= offer.CountToBuySell);
        if (offer.OfferDone)
        {
            RemoveRemoteOffer(message.OwnerCompanySlot, message.OfferId);
            return;
        }

        InvokeOfferUiRefresh(manager);
    }

    private void RemoveRemoteOffer(int ownerCompanySlot, int offerId)
    {
        var manager = FindMarketOfferManager();
        var offer = FindOffer(manager, ownerCompanySlot, offerId);
        if (manager == null || offer == null)
        {
            return;
        }

        manager.Offerts.Remove(offer);
        InvokeOfferUiRefresh(manager);
    }

    private OfferType? FindOffer(MarketOfferManagerType? manager, int ownerCompanySlot, int offerId)
    {
        return manager?.Offerts.FirstOrDefault(offer =>
            offer != null &&
            offer.ID == offerId &&
            _companyOwnershipService.TryGetSlotForCompany(offer.Company, out var slot) &&
            slot == ownerCompanySlot);
    }

    private static void SetOfferProgress(OfferType offer, double countCompleted, bool offerDone)
    {
        CountCompletedRef(offer) = Math.Max(0, countCompleted);
        OfferDoneRef(offer) = offerDone;
    }

    private static MarketOfferManagerType? FindMarketOfferManager()
    {
        return UnityEngine.Object.FindObjectOfType<MarketOfferManagerType>();
    }

    private static void InvokeOfferUiRefresh(MarketOfferManagerType manager)
    {
        manager.RefreshForUI();
    }

    private static void SubscribeOfferPartComplete(MarketOfferManagerType manager, OfferType offer)
    {
        var method = AccessTools.Method(typeof(MarketOfferManagerType), "OfferOnOnPartComplete");
        if (method == null)
        {
            return;
        }

        var handler = (Action<OfferType, double>)Delegate.CreateDelegate(typeof(Action<OfferType, double>), manager, method);
        offer.OnPartComplete += handler;
    }
}
