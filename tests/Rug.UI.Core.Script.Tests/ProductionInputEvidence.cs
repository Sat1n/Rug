using Rug.UI.Core.Models;

/// <summary>Diagnostic consumer aggregates only; never changes production classification or input.</summary>
internal sealed class ProductionInputEvidence
{
    internal sealed record Counts(long Generation = 0, long ForeignGenerations = 0,
        long ForegroundKeyboard = 0, long BackgroundKeyboard = 0,
        long ForegroundMouse = 0, long BackgroundMouse = 0,
        long SoftwareMoves = 0, long SoftwareMouseDown = 0, long SoftwareMouseUp = 0,
        long SoftwareF8Down = 0, long SoftwareF8Up = 0, long SoftwareBDown = 0, long SoftwareBUp = 0)
    {
        internal Counts Delta(Counts before) => new(Generation, ForeignGenerations - before.ForeignGenerations,
            ForegroundKeyboard - before.ForegroundKeyboard, BackgroundKeyboard - before.BackgroundKeyboard,
            ForegroundMouse - before.ForegroundMouse, BackgroundMouse - before.BackgroundMouse,
            SoftwareMoves - before.SoftwareMoves, SoftwareMouseDown - before.SoftwareMouseDown, SoftwareMouseUp - before.SoftwareMouseUp,
            SoftwareF8Down - before.SoftwareF8Down, SoftwareF8Up - before.SoftwareF8Up,
            SoftwareBDown - before.SoftwareBDown, SoftwareBUp - before.SoftwareBUp);
    }
    private readonly object _gate = new();
    private Counts _counts = new();
    internal Counts Read() { lock (_gate) return _counts; }

    // @shape detached aggregates: only known software F8/B keys are distinguished; human keys remain totals.
    // @source ../../Rug.UI.Core/Models/InputEvents.cs#class:InputObservation
    internal void Observe(InputSourceEvent e)
    {
        lock (_gate)
        {
            var c = _counts;
            if (c.Generation == 0) c = c with { Generation = e.Generation };
            if (c.Generation != e.Generation) c = c with { ForeignGenerations = c.ForeignGenerations + 1 };
            var o = e.Observation;
            if (o.IsCandidateInput)
            {
                if (o.Kind == InputEventKind.Keyboard)
                    c = o.Foreground ? c with { ForegroundKeyboard = c.ForegroundKeyboard + e.Samples }
                        : c with { BackgroundKeyboard = c.BackgroundKeyboard + e.Samples };
                else
                    c = o.Foreground ? c with { ForegroundMouse = c.ForegroundMouse + e.Samples }
                        : c with { BackgroundMouse = c.BackgroundMouse + e.Samples };
            }
            else if (o.IsRugSoftwareInput)
            {
                if (o.Kind == InputEventKind.Mouse)
                    c = c with
                    {
                        SoftwareMoves = c.SoftwareMoves + (o.IsPureMovement ? e.Samples : 0),
                        SoftwareMouseDown = c.SoftwareMouseDown + System.Numerics.BitOperations.PopCount((uint)(o.ButtonFlags & 0x155)),
                        SoftwareMouseUp = c.SoftwareMouseUp + System.Numerics.BitOperations.PopCount((uint)(o.ButtonFlags & 0x2aa))
                    };
                else if (o.VirtualKey == 119)
                    c = o.KeyReleased ? c with { SoftwareF8Up = c.SoftwareF8Up + e.Samples }
                        : c with { SoftwareF8Down = c.SoftwareF8Down + e.Samples };
                else if (o.VirtualKey == 66)
                    c = o.KeyReleased ? c with { SoftwareBUp = c.SoftwareBUp + e.Samples }
                        : c with { SoftwareBDown = c.SoftwareBDown + e.Samples };
            }
            _counts = c;
        }
    }
    internal static bool HumanCountsComplete(Counts c) => c.ForegroundKeyboard > 0 && c.BackgroundKeyboard > 0
        && c.ForegroundMouse > 0 && c.BackgroundMouse > 0 && c.ForeignGenerations == 0;
    internal static bool Continuous(long generation, InputEventSourceSnapshot s, Counts c) => s.Generation == generation
        && (c.Generation == 0 || c.Generation == generation) && c.ForeignGenerations == 0
        && s.State == InputEventSourceState.Ready && s.Fault == InputEventSourceFault.None
        && s.Error is null && s.Overflows == 0 && s.ConsumerErrors == 0;
    // @shape each unchanged SendInput round: two moves, click down/up, extra mouse release, key down/up.
    // @source RawInputDiagnostics.cs#class:RawInputDiagnostics
    internal static bool RawComplete(bool post, int key, RawInputDiagnostics.RawEvidence raw, Counts detail)
    {
        if (raw.CandidateKeyboard != 0 || raw.CandidateMouse != 0 || detail.ForeignGenerations != 0) return false;
        if (post) return raw.SoftwareKeyboard == 0 && raw.SoftwareMouse == 0 && raw.SoftwareKeyDown == 0
            && raw.SoftwareKeyUp == 0 && detail.SoftwareMoves == 0 && detail.SoftwareMouseDown == 0 && detail.SoftwareMouseUp == 0
            && detail.SoftwareF8Down == 0 && detail.SoftwareF8Up == 0 && detail.SoftwareBDown == 0 && detail.SoftwareBUp == 0;
        bool testedKey = key == 119 ? detail.SoftwareF8Down == 1 && detail.SoftwareF8Up == 1
            && detail.SoftwareBDown == 0 && detail.SoftwareBUp == 0
            : key == 66 && detail.SoftwareBDown == 1 && detail.SoftwareBUp == 1
            && detail.SoftwareF8Down == 0 && detail.SoftwareF8Up == 0;
        return raw.SoftwareKeyboard == 2 && raw.SoftwareMouse == 5 && raw.SoftwareKeyDown == 1 && raw.SoftwareKeyUp == 1
            && detail.SoftwareMoves == 2 && detail.SoftwareMouseDown == 1 && detail.SoftwareMouseUp == 2 && testedKey;
    }
    internal static bool TargetComplete(long down, long up) => down == 1 && up == 1;
}
