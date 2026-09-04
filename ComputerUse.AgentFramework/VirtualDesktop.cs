namespace ComputerUse.AgentFramework;

public sealed record ScreenElement(string Label, int X, int Y);

public sealed record ScreenSnapshot(
    string Screen,
    bool PopupOpen,
    bool DarkMode,
    IReadOnlyList<ScreenElement> Elements);

public sealed record GuiAction(string Kind, int X, int Y);

public sealed record ActionObservation(
    ScreenSnapshot Before,
    GuiAction Action,
    bool Applied,
    string Message,
    ScreenSnapshot After);

public sealed record ComputerUseResult(bool Completed, IReadOnlyList<ActionObservation> Steps);

public sealed class VirtualDesktop
{
    private string screen = "Home";

    public bool PopupOpen { get; private set; } = true;
    public bool DarkMode { get; private set; }

    public ScreenSnapshot Capture()
    {
        ScreenElement[] elements = PopupOpen
            ? [new("Accept", 40, 10), new("Decline", 55, 10)]
            : screen == "Home"
                ? [new("Settings", 10, 5)]
                : [new("Dark mode", 20, 8)];

        return new(screen, PopupOpen, DarkMode, elements);
    }

    public (bool Applied, string Message) Apply(GuiAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!string.Equals(action.Kind, "click", StringComparison.OrdinalIgnoreCase))
            return (false, "Only click actions are allowed.");

        var target = Capture().Elements.FirstOrDefault(element => element.X == action.X && element.Y == action.Y);
        if (target is null)
            return (false, "No visible element exists at those coordinates.");

        switch (target.Label)
        {
            case "Accept":
            case "Decline":
                PopupOpen = false;
                break;
            case "Settings":
                screen = "Settings";
                break;
            case "Dark mode":
                DarkMode = true;
                break;
        }

        return (true, $"Clicked {target.Label}.");
    }
}

public sealed class ComputerUseRunner
{
    public async Task<ComputerUseResult> RunAsync(
        VirtualDesktop desktop,
        Func<ScreenSnapshot, CancellationToken, Task<GuiAction>> propose,
        Func<ScreenSnapshot, bool> goalReached,
        int maxSteps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(propose);
        ArgumentNullException.ThrowIfNull(goalReached);
        if (maxSteps <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSteps));

        var observations = new List<ActionObservation>();
        if (goalReached(desktop.Capture()))
            return new(true, observations);

        for (var step = 0; step < maxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = desktop.Capture();
            var action = await propose(before, cancellationToken);
            var (applied, message) = desktop.Apply(action);
            var after = desktop.Capture();
            observations.Add(new(before, action, applied, message, after));

            if (goalReached(after))
                return new(true, observations);
        }

        return new(false, observations);
    }
}
