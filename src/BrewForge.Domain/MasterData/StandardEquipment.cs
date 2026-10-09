using BrewForge.Domain.Common;

namespace BrewForge.Domain.MasterData;

/// <summary>The <c>dosing_unit</c> enumeration.</summary>
public enum DosingUnit
{
    [Code("g")] Gram,
    [Code("ml")] Millilitre,
    [Code("sec")] Second,
    [Code("degC")] DegreeCelsius,
    [Code("bar")] Bar,
}

/// <summary>
/// An equipment class of the chain-wide standard profile, with the dosing
/// thresholds the validator checks against (BR-10).
/// </summary>
public sealed class StandardEquipment : INeverDeleted
{
    private StandardEquipment() { }

    public long Id { get; private set; }
    public string EquipmentCode { get; private set; } = null!;

    /// <summary>Unique and stable: <c>recipe_step.equipment_class</c> references it.</summary>
    public string EquipmentClass { get; private set; } = null!;
    public decimal MinThreshold { get; private set; }
    public decimal MaxThreshold { get; private set; }
    public DosingUnit DosingUnit { get; private set; }
    public CatalogStatus Status { get; private set; } = CatalogStatus.Active;

    public bool IsActive => Status == CatalogStatus.Active;

    string INeverDeleted.RetentionRule => "BR-16";

    public static StandardEquipment Create(string equipmentCode, string equipmentClass,
        decimal minThreshold, decimal maxThreshold, DosingUnit dosingUnit)
    {
        equipmentCode = equipmentCode?.Trim() ?? "";
        equipmentClass = equipmentClass?.Trim() ?? "";
        new FieldErrors()
            .RequiredMax("equipmentCode", equipmentCode, 24)
            .RequiredMax("equipmentClass", equipmentClass, 40)
            .ThrowIfAny();

        var equipment = new StandardEquipment
        {
            EquipmentCode = equipmentCode,
            EquipmentClass = equipmentClass,
        };
        equipment.Update(minThreshold, maxThreshold, dosingUnit);
        return equipment;
    }

    public void Update(decimal minThreshold, decimal maxThreshold, DosingUnit dosingUnit)
    {
        new FieldErrors()
            .Check(minThreshold >= 0, "minThreshold", "must not be negative")
            .Check(minThreshold <= maxThreshold, "maxThreshold", "must be greater than or equal to minThreshold")
            .Check(Enum.IsDefined(dosingUnit), "dosingUnit", "is not a valid dosing unit")
            .ThrowIfAny();

        MinThreshold = minThreshold;
        MaxThreshold = maxThreshold;
        DosingUnit = dosingUnit;
    }

    /// <summary>An equipment class is never deleted, only deactivated (BR-16).</summary>
    public void Deactivate() => Status = CatalogStatus.Inactive;

    public void Reactivate() => Status = CatalogStatus.Active;
}
