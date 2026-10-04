namespace UniversalHost.Models;

public enum CurveMeasurementMode
{
    None,
    MouseCoordinates,
    NearestPoint,
    VerticalCursors,
    HorizontalCursors,
}

public sealed record CurveMeasurementReadout(
    string Title,
    string Target,
    string First,
    string Second,
    string DeltaX,
    string DeltaY,
    string Status)
{
    public static CurveMeasurementReadout Empty { get; } = new("", "", "", "", "", "", "");
    public bool HasTarget => Target.Length > 0;
    public bool HasSecond => Second.Length > 0;
    public bool HasDeltaX => DeltaX.Length > 0;
    public bool HasDeltaY => DeltaY.Length > 0;
    public bool HasStatus => Status.Length > 0;
}
