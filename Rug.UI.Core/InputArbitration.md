---
id: rug_input_arbitration
type: logic_node
inputs: [rug_ui_core, rug_ui]
outputs: [rug_ui_core]
tags: [input, concurrency, takeover]
---

# Shared input arbitration (Task 3.5-05A)

[IInputArbiter](Abstractions/IInputArbiter.cs#interface:IInputArbiter) is a trusted
host service, implemented by [InputArbiter](Services/InputArbiter.cs#class:InputArbiter).
Production composition shares one arbiter between scheduler/Lua and DevTools.
[InputOwner](Models/InputOwnership.cs#class:InputOwner) contains a host principal/source (UI/Lua/Agent/MCP/DevTools), and for plugins the complete
`RunIdentity`. Owners, window bindings, leases and host control methods are never
decoded from Lua, schema command parameters or Gateway request JSON.
Authorization and arbitration remain separate checks: a lease grants no permission.

## Resource identity and fairness

The target resource key is the actual HWND, independent of plugin, instance or
targetBindingId. [InputWindowValidator](Services/InputWindowValidator.cs#class:InputWindowValidator) stores one lifetime property on the actual
window. Destruction removes it; a reused HWND cannot validate the old stamp.
The fixed property name avoids accumulating properties across host restarts.
No managed per-window registry is retained. Binding fails closed if user32 denies
property access (including protected/elevated targets). `IInputWindowValidator`
is replaceable for host/test environments. This is a lifetime guard, not an
adversarial window authentication or cross-process arbitration mechanism.

| Competing actions | Same HWND | Different HWNDs |
|---|---|---|
| SendInput / SendInput | Serial | Serial |
| PostMessage / PostMessage | Serial | Parallel |
| SendInput / PostMessage | Serial | Parallel |

The queue admits at most 256 waiting requests by default. Conflict FIFO means
later requests cannot pass earlier requests sharing either resource; disjoint
resources can progress concurrently. Window and optional global foreground are
reserved atomically under one short state lock, eliminating partial acquisition
and lock-order deadlocks. No permission callback, service, UI interaction or
cancellation callback runs under that lock. Cancellation/deadline removes a
waiter and its linked-token registration; it never leaves a queue placeholder.

## Compound actions and late native work

`InputBridge` holds one lease across mode selection, foreground confirmation,
mouse movement/click or KeyDown/hold/KeyUp. It rechecks live authorization, the
run cancellation token and window lifetime after queuing and before input steps.
Client pixel validation and existing foreground checks remain in effect.
Necessarily uncancellable releases occur inside the lease, without demanding
current permission or automation availability. Native Click/KeyPress can already
perform a release; a final explicit release protects their failure paths.

Gateway deadlines cancel the dispatched bridge token but do not release its
lease. The bridge awaits the actual input task and cleanup even if Gateway has
already reported cancellation/timeout. Scheduler Stop also awaits the runner's
cleanup rather than disposing its native input handle after a fixed timeout.
Waiting input has no side effects and reports `OutcomeUncertain=false`; once
foreground activation is attempted or native input starts, any non-success
Gateway result remains uncertain and never auto-retries. This includes a later
permission or target recheck rejecting the action after movement. Mode binding,
coordinate validation and queued admission alone do not mark execution started.
Foreground confirmation can fail after changing focus, so marking occurs before
that call. Retained duplicate results preserve this flag without repeating input.
The default bridge uses [ForegroundWindowActivation](Native/ForegroundWindowActivation.cs#class:ForegroundWindowActivation):
one `SetForegroundWindow` attempt followed by cancellable foreground queries,
at most one second, using monotonic time and asynchronous 10 ms waits. Cross-input-queue
activation may finish after the API returns ([Windows explanation](https://devblogs.microsoft.com/oldnewthing/20161118-00/?p=94745)).
The lease remains held, and each query rechecks live admission. A declined call or
unconfirmed activation faults without sending a key/mouse action; host failure logs
include the activation return, target/observed HWND and elapsed confirmation time.
Windows foreground restrictions still apply. This is neither an activation retry
nor an input-detection grace period; takeover/cancellation interrupts the wait.
`InputUnavailable` is a stable rejection code for closed input admission/invalid
target lifetime/queue capacity. Existing permission, identity and parameter
errors remain distinct. Deduplication still pins live underlying work.

06A ordinary Pause keeps already admitted input/queues under these checks until
actual work and releases finish; only then is Paused confirmed. New run input is
rejected while pause is pending/confirmed. Gateway timeout does not shorten this
permit, and Stop waits for actual drain before disposal. Resume rechecks the original
target stamp/control and current grants. Human takeover still stops Paused input
runs; Return does not revive them. See [lifecycle contract](Lifecycle.md).

Synchronous C-ABI calls cannot be interrupted once entered. A takeover may wait
for such a call, and residual native input can occur before it completes. A call
that never completes keeps its lease and native handle alive; the UI stays in
TakingOver and cannot hand control back. A failed key/button release quarantines
its resource, closes all new input and sets CleanupFailed. This unit has no
unsafe force-release or fault-reset button; restart/manual investigation is
required. Other in-flight work is cancelled and observed.

## Host takeover and return

The state transition is AutomationAvailable -> TakingOver -> ManualControl.
`TakeOverAsync` atomically closes new admission, then cancels the old input epoch
outside the lock. All queued input and input-capable runtime lifetimes use that
epoch. The scheduler waits for startup cancellation, stops runs declaring `input`,
and awaits their resource cleanup. Timer/vision-only runs are preserved,
including unrelated visual startups still waiting on capture initialization.
DevTools synthetic input is also closed. ManualControl is published only after
both active leases and affected run cleanup finish; failures publish CleanupFailed.

The host footer supplies Manual takeover / Return control. State notifications
carry a revision via the snapshot; the UI rereads current state on its dispatcher
so an older queued notification cannot overwrite newer state. Repeated clicks
are idempotent. Only ManualControl with no waiting/active input can return to
AutomationAvailable; returning creates a fresh cancellation epoch. Old requests,
tokens and stopped runs stay invalid. Grants are unchanged, and users must start
a fresh run explicitly. Background hot reload cannot start input during takeover.
These host methods are not registered as schema/Lua/Agent/MCP commands.

## DevTools and verification

The host uses one DevTools view-model/controller. Its actual mouse, drag,
keyboard and stress-test input execute inside the same arbiter, with a separate
trusted DevTools owner. Mouse/keyboard test sequences reserve their compound inputs once;
stress capture/OCR runs outside arbitration, reserving only each move/click.
Inner native calls never requeue. Binding/mode changes invalidate pending work,
native rebinding and foreground activation happen inside the lease, and each
input step validates the lease. Pure trajectory planning needs no input lease.
The low-level `IInputService` remains a trusted implementation boundary; plugins
and external adapters receive only Gateway operations, not this raw service.

The script suite's `InputArbitrationTests` uses controlled task barriers for
conflicts, FIFO, cancellation, queue capacity, revoked grants, old targets,
late input, handle lifetime, failed release, repeated takeover and subscription
cleanup. `NativeInputTakeoverTests` creates/destroys its own message-pumped Win32
window and checks real PostMessage click and cancelled key release when the
Debug x64 native DLL is available. The UI suite's `--takeover-ui` drives actual
host controls and verifies repeated state transitions preserve timer-only runs.

05A supplied only active host takeover. Task 3.5-05B-02 adds
[candidate external input detection](InputIntervention.md): the production host
registers a detector readiness guard, checked outside arbitration locks before
queueing, after waiting, before lease steps and on Return control. Source failure
cannot be bypassed by returning control. Host input sessions span entire input
runs/DevTools sequences, including gaps, and drain after actual cleanup before
ManualControl. Candidate external input is not authenticated physical input;
diagnostic Hook gaps remain separate; 05B-02 human UI acceptance is user-confirmed.
