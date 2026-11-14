namespace Goggles.Desktop.ViewModels;

/// <summary>Which tab the detail page is showing. Only <see cref="Overview"/> has real content —
/// the others render an inert "not available yet" placeholder.</summary>
public enum DetailTab
{
    Overview,
    Progress,
    Extras,
}
