using Godot;

namespace LexiForge.Autoload;

/// <summary>
/// Global typed signals (GDD 11.4). Views listen; services never hold a
/// reference to a view. Signal payloads are restricted to Godot-marshallable
/// primitives (int / float / string / bool) — lists are passed as CSV strings
/// so no managed object ever crosses a signal boundary.
///
/// Registered FIRST in project.godot so every other autoload can emit here in
/// its _Ready() without a null check race.
/// </summary>
public sealed partial class EventBus : Node
{
    public static EventBus Instance { get; private set; } = null!;

    [Signal] public delegate void DataReadyEventHandler();
    [Signal] public delegate void ForgeStartedEventHandler(int forgeId, int runIndex);
    [Signal] public delegate void ForgeCompletedEventHandler(int forgeId, int coins, int stars);
    [Signal] public delegate void WordAcceptedEventHandler(string word, int flowHundredths, int bonusWords);
    [Signal] public delegate void WordRejectedEventHandler(string reason);
    [Signal] public delegate void HintGrantedEventHandler(int slotIndex, string letter);
    [Signal] public delegate void CoinsChangedEventHandler(int delta, int newBalance, string source);
    [Signal] public delegate void MarksChangedEventHandler(int delta, int newBalance, string source);
    [Signal] public delegate void RewardGrantedEventHandler(string placement, string rewardToken);
    [Signal] public delegate void HeatChangedEventHandler(int newHeat);
    [Signal] public delegate void DraftOfferedEventHandler(string runeIdsCsv);
    [Signal] public delegate void RunEndedEventHandler(int totalScore, int forges, string runeIdsCsv);
    [Signal] public delegate void ScreenChangedEventHandler(string screenName);
    [Signal] public delegate void PauseRequestedEventHandler();

    public override void _Ready()
    {
        Instance = this;
        // An EventBus must never be paused by a parent node or a paused screen.
        ProcessMode = ProcessModeEnum.Always;
    }
}
