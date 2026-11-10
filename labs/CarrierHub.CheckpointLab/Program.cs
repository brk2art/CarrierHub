using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

// Checkpoints: a workflow stops to wait for a person, is saved to disk, and continues in ANOTHER process.
//   dotnet run --project labs/CarrierHub.CheckpointLab -- start
//   dotnet run --project labs/CarrierHub.CheckpointLab -- resume approve     (or: resume reject)
// effects.log records every side effect, so we can see exactly what ran, and how often.
var mode = args.ElementAtOrDefault(0) ?? "start";
var data = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "lab-data"));
var effects = Path.Combine(data.FullName, "effects.log");   // one line per side effect
var pointer = Path.Combine(data.FullName, "waiting.txt");   // the checkpoint that waits for a person

using var store = new FileSystemJsonCheckpointStore(data);   // checkpoints as JSON files on disk
var checkpoints = CheckpointManager.CreateJson(store);

if (mode == "start")
{
    File.Delete(effects);
    await using var run = await InProcessExecution.RunStreamingAsync(
        BuildWorkflow(effects), new ClaimRequest("ORDER-1001", "Late"), checkpoints);

    var waiting = false;
    await foreach (var evt in run.WatchStreamAsync())
    {
        Console.WriteLine($"  event: {evt.GetType().Name}{(evt is SuperStepCompletedEvent s ? $" checkpoint={s.CompletionInfo?.Checkpoint?.CheckpointId ?? "none"}" : "")}");
        if (evt is ExecutorFailedEvent or WorkflowErrorEvent)
            Console.WriteLine($"  error: {evt.Data}");

        if (evt is RequestInfoEvent)
        {
            waiting = true;
            Console.WriteLine("The workflow asks a person for approval.");
        }
        else if (waiting && evt is SuperStepCompletedEvent { CompletionInfo.Checkpoint: { } checkpoint })
        {
            File.WriteAllText(pointer, $"{checkpoint.SessionId}\n{checkpoint.CheckpointId}");
            Console.WriteLine($"Saved checkpoint {checkpoint.CheckpointId} (run {checkpoint.SessionId}). The process ends now.");
            break;   // as if the server restarted while the person thinks
        }
    }
}
else
{
    var approve = args.ElementAtOrDefault(1) == "approve";
    var saved = File.ReadAllLines(pointer);
    var checkpoint = new CheckpointInfo(saved[0], saved[1]);   // session id + checkpoint id, as saved by "start"
    Console.WriteLine($"New process. Resuming checkpoint {checkpoint.CheckpointId}.");

    await using var run = await InProcessExecution.ResumeStreamingAsync(BuildWorkflow(effects), checkpoint, checkpoints);
    await foreach (var evt in run.WatchStreamAsync())
    {
        if (evt is RequestInfoEvent request)
        {
            Console.WriteLine("Still waiting after the restart: the approval request came back.");
            await run.SendResponseAsync(request.Request.CreateResponse(new ApprovalDecision(approve, Environment.UserName)));
        }
        else if (evt is WorkflowOutputEvent output)
        {
            Console.WriteLine($"Result: {output.Data}");
            break;
        }
    }
}

Console.WriteLine("effects.log:");
foreach (var line in File.Exists(effects) ? File.ReadAllLines(effects) : [])
    Console.WriteLine($"  {line}");

// The same steps with the same ids every time: a checkpoint can only be resumed into the workflow that made it.
static Workflow BuildWorkflow(string effects)
{
    var check = new CheckExecutor(effects);
    var approval = RequestPort.Create<ApprovalRequest, ApprovalDecision>("approval");
    var open = new OpenExecutor(effects);

    return new WorkflowBuilder(check)
        .AddEdge(check, approval)
        .AddEdge(approval, open)
        .WithOutputFrom(open)
        .Build();
}

public sealed record ClaimRequest(string OrderNumber, string Reason);
public sealed record ApprovalRequest(string OrderNumber, string Reason);
public sealed record ApprovalDecision(bool Approved, string Approver);

// Step 1: checks the claim, keeps it in the workflow's shared state (so it's inside every checkpoint), asks a person.
// Executor<input, output>: what it returns is the message it sends, and the type is declared, as 1.28 requires.
internal sealed class CheckExecutor(string effects) : Executor<ClaimRequest, ApprovalRequest>("check")
{
    public override async ValueTask<ApprovalRequest> HandleAsync(ClaimRequest claim, IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        File.AppendAllText(effects, $"CHECKED {claim.OrderNumber} {claim.Reason}{Environment.NewLine}");
        await context.QueueStateUpdateAsync("claim", claim, scopeName: "Claim", cancellationToken);
        return new ApprovalRequest(claim.OrderNumber, claim.Reason);
    }
}

// Step 3: gets the person's decision, reads the claim back from shared state, and "opens" it, at most once.
// A checkpoint can be resumed more than once, so the side effect needs its own key: here the order + reason,
// like Tracking's unique index on (ShipmentId, Reason).
internal sealed class OpenExecutor(string effects) : Executor<ApprovalDecision>("open")
{
    public override async ValueTask HandleAsync(ApprovalDecision decision, IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var claim = await context.ReadStateAsync<ClaimRequest>("claim", scopeName: "Claim", cancellationToken)
            ?? throw new InvalidOperationException("No claim in the workflow's state.");
        var key = $"{claim.OrderNumber} {claim.Reason}";

        if (!decision.Approved)
        {
            Console.WriteLine($"Result: claim for {key} rejected by {decision.Approver}.");
            return;
        }

        if (File.Exists(effects) && File.ReadLines(effects).Any(line => line.StartsWith($"OPENED {key}")))
        {
            Console.WriteLine($"Result: a claim for {key} is already open. Nothing done.");
            return;
        }

        File.AppendAllText(effects, $"OPENED {key} (approved by {decision.Approver}){Environment.NewLine}");
        Console.WriteLine($"Result: claim opened for {key}.");
    }
}