using HomekoWorld.Core;
using HomekoWorld.Hooks;
using HomekoWorld.Models;

namespace HomekoWorld.Engine;

public sealed class BindingDispatcher : IDisposable
{
    private readonly GlobalKeyboardHook _hook;
    private readonly ComboEngine _engine;
    private readonly AppState    _state;
    private List<Combo> _combos = [];
    private bool           _active;
    private volatile bool  _testPaused;

    public event EventHandler? ActiveToggled;
    public bool Active => _active;
    public bool IsCapturingBinding { get; set; }

    public void PauseForTest()  => _testPaused = true;
    public void ResumeAfterTest() => _testPaused = false;

    public BindingDispatcher(GlobalKeyboardHook hook, ComboEngine engine, AppState state)
    {
        _hook   = hook;
        _engine = engine;
        _state  = state;
        _hook.KeyDown += OnKeyDown;
    }

    public void SetCombos(IEnumerable<Combo> combos) => _combos = [.. combos];

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (_active)
            _hook.Start();
        else
        {
            _engine.CancelAll();
        }
        ActiveToggled?.Invoke(this, EventArgs.Empty);
    }

    private void OnKeyDown(object? sender, HookKeyEventArgs e)
    {
        if (_testPaused) return;
        // Tuş atama modundayken hook hiçbir tuşu yutmaz
        if (IsCapturingBinding) return;

        if (KeyCode.ToName(e.Key) == _state.GlobalStartKey && e.Modifiers == KeyboardModifiers.None)
        {
            SetActive(!_active);
            e.Handled = true;
            return;
        }

        if (!_active) return;

        var keyName = KeyCode.ToName(e.Key);
        if (keyName is null) return;
        var modNames = KeyCode.GetModifiers(e.Modifiers);

        var combo = _combos.FirstOrDefault(c =>
            c.Binding is { IsEmpty: false } b &&
            b.Code == keyName &&
            (b.Modifiers.SequenceEqual(modNames) ||
             // Modifier-as-trigger: binding Modifiers=[], ama tuşun kendisi modifier
             (b.Modifiers.Length == 0 &&
              modNames.Length == 1 &&
              modNames[0] == keyName)));

        if (combo is null) return;

        e.Handled = true;
        _engine.FireAsync(combo);
    }

    public void Dispose()
    {
        _hook.KeyDown -= OnKeyDown;
        _hook.Stop();
    }
}
