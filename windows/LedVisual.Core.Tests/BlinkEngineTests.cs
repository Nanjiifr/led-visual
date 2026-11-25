using LedVisual.Core.Models;
using LedVisual.Core.Services;
using Xunit;

namespace LedVisual.Core.Tests;

internal sealed class MockCapsLockController : ICapsLockLedController
{
    private readonly object _lock = new();
    private bool _currentState;
    private readonly List<bool> _transitions = [];

    public MockCapsLockController(bool initialState = false)
    {
        _currentState = initialState;
    }

    public bool GetState()
    {
        lock (_lock)
        {
            return _currentState;
        }
    }

    public void SetState(bool enabled)
    {
        lock (_lock)
        {
            _currentState = enabled;
            _transitions.Add(enabled);
        }
    }

    public bool FinalState
    {
        get
        {
            lock (_lock)
            {
                return _currentState;
            }
        }
    }

    public IReadOnlyList<bool> RecordedTransitions
    {
        get
        {
            lock (_lock)
            {
                return _transitions.ToArray();
            }
        }
    }
}

public sealed class BlinkEngineTests
{
    [Fact]
    public void SingleAnswerPatternHasNoTrailingLongBlink()
    {
        var timings = new BlinkTimings(
            shortOnMs: 150,
            shortOffMs: 170,
            interAnswerPauseMs: 800,
            errorLongOnMs: 1400,
            preBlinkDarkMs: 250);

        var steps = BlinkPatternBuilder.StepsForAnswers(new[] { 3 }, initiallyOn: false, timings: timings);
        Assert.Equal(
            new[]
            {
                new BlinkStep(true, 150),
                new BlinkStep(false, 170),
                new BlinkStep(true, 150),
                new BlinkStep(false, 170),
                new BlinkStep(true, 150),
                new BlinkStep(false, 0)
            },
            steps);
    }

    [Fact]
    public void MultiSelectPatternSeparatesGroupsWithLongPause()
    {
        var timings = new BlinkTimings(
            shortOnMs: 150,
            shortOffMs: 170,
            interAnswerPauseMs: 800,
            errorLongOnMs: 1400,
            preBlinkDarkMs: 250);

        var steps = BlinkPatternBuilder.StepsForAnswers(new[] { 1, 3 }, initiallyOn: false, timings: timings);
        Assert.Equal(
            new[]
            {
                new BlinkStep(true, 150),
                new BlinkStep(false, 800),
                new BlinkStep(true, 150),
                new BlinkStep(false, 170),
                new BlinkStep(true, 150),
                new BlinkStep(false, 170),
                new BlinkStep(true, 150),
                new BlinkStep(false, 0)
            },
            steps);
    }

    [Fact]
    public void ErrorPatternIsSingleLongBlink()
    {
        var timings = new BlinkTimings(errorLongOnMs: 1500, preBlinkDarkMs: 250);
        var steps = BlinkPatternBuilder.StepsForError(initiallyOn: false, timings: timings);
        Assert.Equal(
            new[]
            {
                new BlinkStep(true, 1500),
                new BlinkStep(false, 0)
            },
            steps);
    }

    [Fact]
    public async Task EngineRestoresInitialCapsLockOnState()
    {
        var mock = new MockCapsLockController(initialState: true);
        var engine = new BlinkEngine(mock, (_, _) => Task.CompletedTask);

        await engine.BlinkAnswersAsync(new[] { 2 }, BlinkTimings.Default);

        Assert.True(mock.FinalState);
        // Initial dark baseline, 2 blinks (ON/OFF x2), final restore ON
        Assert.Equal(new[] { false, true, false, true, false, true }, mock.RecordedTransitions);
    }

    [Fact]
    public async Task EngineRestoresInitialCapsLockOffStateOnError()
    {
        var mock = new MockCapsLockController(initialState: false);
        var engine = new BlinkEngine(mock, (_, _) => Task.CompletedTask);

        await engine.BlinkErrorAsync(BlinkTimings.Default);

        Assert.False(mock.FinalState);
        Assert.Equal(new[] { true, false, false }, mock.RecordedTransitions);
    }
}
