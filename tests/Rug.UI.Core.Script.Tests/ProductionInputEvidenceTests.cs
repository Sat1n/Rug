using Rug.UI.Core.Models;

internal static class ProductionInputEvidenceTests
{
    internal static void Run()
    {
        var evidence = new ProductionInputEvidence();
        var before = evidence.Read();
        long sequence = 0;
        void Observe(InputObservation o, long samples = 1, long generation = 1)
            => evidence.Observe(new(generation, ++sequence, sequence, o, samples));
        uint tag = InputObservation.RugSoftwareInputTag;
        Observe(new(InputEventKind.Keyboard, 1, true, VirtualKey: 119, ExtraInformation: tag));
        Observe(new(InputEventKind.Keyboard, 1, true, VirtualKey: 119, KeyFlags: 1, ExtraInformation: tag));
        Observe(new(InputEventKind.Mouse, 1, true, X: 5, ExtraInformation: tag), 2); // Coalesced moves retain samples.
        Observe(new(InputEventKind.Mouse, 1, true, ButtonFlags: 1, ExtraInformation: tag));
        Observe(new(InputEventKind.Mouse, 1, true, ButtonFlags: 2, ExtraInformation: tag));
        Observe(new(InputEventKind.Mouse, 1, true, ButtonFlags: 2, ExtraInformation: tag));
        var f8 = evidence.Read().Delta(before);
        var raw = new RawInputDiagnostics.RawEvidence(0, 0, 2, 5, 1, 1);
        RawInputTests.Check(ProductionInputEvidence.RawComplete(false, 119, raw, f8), "complete F8 software evidence rejected");
        RawInputTests.Check(!ProductionInputEvidence.RawComplete(false, 66, raw, f8), "wrong software test key accepted");
        RawInputTests.Check(!ProductionInputEvidence.RawComplete(false, 119, raw, f8 with { SoftwareMouseUp = 1 }), "missing safety release accepted");
        RawInputTests.Check(!ProductionInputEvidence.RawComplete(false, 119, raw with { CandidateKeyboard = 1 }, f8), "candidate counted as software success");
        RawInputTests.Check(!ProductionInputEvidence.RawComplete(false, 119, raw with { SoftwareKeyboard = 3 }, f8), "excess software input accepted");
        RawInputTests.Check(!ProductionInputEvidence.RawComplete(false, 119, raw, f8 with { SoftwareMoves = 1 }), "incomplete move evidence accepted");
        RawInputTests.Check(ProductionInputEvidence.TargetComplete(1, 1)
            && !ProductionInputEvidence.TargetComplete(0, 1) && !ProductionInputEvidence.TargetComplete(2, 1), "specific target must require exactly one down/up");
        var zero = new RawInputDiagnostics.RawEvidence(0, 0, 0, 0, 0, 0);
        RawInputTests.Check(ProductionInputEvidence.RawComplete(true, 119, zero, new()), "zero Raw PostMessage evidence rejected");
        RawInputTests.Check(!ProductionInputEvidence.RawComplete(true, 119, zero, f8), "PostMessage software emissions accepted");

        before = evidence.Read();
        Observe(new(InputEventKind.Keyboard, 1, false, VirtualKey: 66, ExtraInformation: tag));
        Observe(new(InputEventKind.Keyboard, 1, false, VirtualKey: 66, KeyFlags: 1, ExtraInformation: tag));
        Observe(new(InputEventKind.Mouse, 1, false, X: 5, ExtraInformation: tag), 2);
        Observe(new(InputEventKind.Mouse, 1, false, ButtonFlags: 1, ExtraInformation: tag));
        Observe(new(InputEventKind.Mouse, 1, false, ButtonFlags: 2, ExtraInformation: tag));
        Observe(new(InputEventKind.Mouse, 1, false, ButtonFlags: 2, ExtraInformation: tag));
        RawInputTests.Check(ProductionInputEvidence.RawComplete(false, 66, raw, evidence.Read().Delta(before)), "B isolated delta leaked F8 counts");

        before = evidence.Read();
        foreach (bool foreground in new[] { true, false })
        {
            Observe(new(InputEventKind.Keyboard, 1, foreground, ExtraInformation: 42)); // Unknown marker stays a candidate.
            Observe(new(InputEventKind.Keyboard, 0, foreground, KeyFlags: 1));
            Observe(new(InputEventKind.Mouse, 1, foreground, X: 3), 3);
        }
        Observe(new(InputEventKind.DeviceArrived, 1, true));
        var human = evidence.Read().Delta(before);
        RawInputTests.Check(ProductionInputEvidence.HumanCountsComplete(human)
            && human.ForegroundKeyboard == 2 && human.BackgroundKeyboard == 2
            && human.ForegroundMouse == 3 && human.BackgroundMouse == 3, "candidate class/foreground/sample counters incorrect");
        RawInputTests.Check(!ProductionInputEvidence.HumanCountsComplete(human with { BackgroundMouse = 0 }), "foreground-only evidence accepted");
        RawInputTests.Check(f8.ForegroundKeyboard == 0 && f8.ForegroundMouse == 0, "software counted as human evidence");
        var saved = evidence.Read();
        Parallel.For(0, 1000, _ => evidence.Observe(new(1, 99, 99, new(InputEventKind.Keyboard, 1, false))));
        RawInputTests.Check(evidence.Read().BackgroundKeyboard == saved.BackgroundKeyboard + 1000
            && saved.BackgroundKeyboard == 2, "concurrent aggregates lost data or mutated snapshot");
        var healthy = new InputEventSourceSnapshot(InputEventSourceState.Ready, 1, 1, InputEventSourceFault.None,
            null, 0, 0, 0, 0, 0, 0, 0, 0);
        RawInputTests.Check(ProductionInputEvidence.Continuous(1, healthy, saved), "healthy continuity rejected");
        RawInputTests.Check(!ProductionInputEvidence.Continuous(1, healthy with { Generation = 2 }, saved)
            && !ProductionInputEvidence.Continuous(1, healthy with { Overflows = 1 }, saved)
            && !ProductionInputEvidence.Continuous(1, healthy with { ConsumerErrors = 1 }, saved)
            && !ProductionInputEvidence.Continuous(1, healthy with { Fault = InputEventSourceFault.OwnershipLost }, saved), "continuity falsely accepted");
        Observe(new(InputEventKind.Keyboard, 1, false), generation: 2);
        RawInputTests.Check(evidence.Read().ForeignGenerations == 1
            && !ProductionInputEvidence.Continuous(1, healthy, evidence.Read()), "mixed-generation delivery hidden");
        Console.WriteLine("Production diagnostic aggregate/verdict tests passed; synthetic tests are not human or software acceptance");
    }
}
