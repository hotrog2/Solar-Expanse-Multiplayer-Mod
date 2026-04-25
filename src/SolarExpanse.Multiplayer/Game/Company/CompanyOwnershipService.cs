using System;
using System.Collections.Generic;
using System.Linq;
using Manager;
using UnityEngine;

using GameCompany = global::Game.Company;

namespace SolarExpanse.Multiplayer.Game.Company;

public sealed class CompanyOwnershipService
{
    private readonly Dictionary<Guid, int> _sessionCompanySlots = new Dictionary<Guid, int>();

    public int LocalCompanySlot { get; private set; } = -1;

    public void SetLocalCompanySlot(int companySlot)
    {
        LocalCompanySlot = companySlot;
    }

    public int AssignCompanySlot(Guid sessionId, int requestedSlot, IReadOnlyCollection<int> inUseSlots, int maxCompanies)
    {
        var companyCount = GetKnownCompanies().Count;
        if (companyCount <= 0)
        {
            companyCount = Math.Max(4, inUseSlots.Count + 1);
        }

        if (maxCompanies > 0)
        {
            companyCount = Math.Min(companyCount, maxCompanies);
        }

        if (inUseSlots.Count >= companyCount)
        {
            return -1;
        }

        var assigned = requestedSlot;
        if (assigned < 0 || assigned >= companyCount || inUseSlots.Contains(assigned))
        {
            assigned = Enumerable.Range(0, companyCount).First(slot => !inUseSlots.Contains(slot));
        }

        _sessionCompanySlots[sessionId] = assigned;
        return assigned;
    }

    public IReadOnlyList<CompanyDescriptor> GetKnownCompanies()
    {
        var manager = UnityEngine.Object.FindObjectOfType<GameManager>();
        if (manager == null || manager.Companies == null)
        {
            return Array.Empty<CompanyDescriptor>();
        }

        return manager.Companies
            .Select((company, index) => new CompanyDescriptor(index, company.ID, company.name))
            .ToArray();
    }

    public bool TryGetCompanyBySlot(int slot, out GameCompany? company)
    {
        company = null;

        var manager = UnityEngine.Object.FindObjectOfType<GameManager>();
        if (manager == null || manager.Companies == null || slot < 0 || slot >= manager.Companies.Count)
        {
            return false;
        }

        company = manager.Companies[slot];
        return company != null;
    }

    public bool TryGetSlotForCompany(GameCompany? company, out int slot)
    {
        slot = -1;
        if (company == null)
        {
            return false;
        }

        var manager = UnityEngine.Object.FindObjectOfType<GameManager>();
        if (manager == null || manager.Companies == null)
        {
            return false;
        }

        for (var i = 0; i < manager.Companies.Count; i++)
        {
            if (ReferenceEquals(manager.Companies[i], company))
            {
                slot = i;
                return true;
            }
        }

        return false;
    }
}

public sealed class CompanyDescriptor
{
    public CompanyDescriptor(int slot, string id, string displayName)
    {
        Slot = slot;
        Id = id;
        DisplayName = displayName;
    }

    public int Slot { get; }
    public string Id { get; }
    public string DisplayName { get; }
}
