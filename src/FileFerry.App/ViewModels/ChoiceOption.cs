using FileFerry.App.Resources;
using FileFerry.Core.Domain;

namespace FileFerry.App.ViewModels;

/// <summary>
/// Schedule choice chip (add-pair wizard step 3 / settings screen): a <see cref="SyncSchedule"/> value + selected state.
/// </summary>
public sealed class ChoiceOption : OptionChip
{
    /// <summary>
    /// Creates the chip from a schedule value and a localized label.
    /// </summary>
    public ChoiceOption(SyncSchedule value, string label)
        : base(label) => Value = value;

    public SyncSchedule Value { get; }

    /// <summary>
    /// Labels of the known schedules (ordered from most to least frequent, per add-pair wizard step 3).
    /// </summary>
    public static IReadOnlyList<(SyncSchedule Value, string Label)> KnownSchedules() =>
    [
        (SyncSchedule.Every15Minutes, AppStrings.Schedule15),
        (SyncSchedule.Every30Minutes, AppStrings.Schedule30),
        (SyncSchedule.Hourly, AppStrings.Schedule60),
        (SyncSchedule.Manual, AppStrings.ScheduleManual),
    ];
}
