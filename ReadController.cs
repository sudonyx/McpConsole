using System.Text.Json;

internal sealed record ControllerReply(string Kind, string Value, JsonElement Result = default);

internal sealed class ReadController(ChatSettings settings,
    Func<ReadCapability, CancellationToken, Task<string?>> currentDefinitionHash,
    Func<ReadCapability, CancellationToken, Task<JsonElement>> invoke,
    Action<Guid?, string, object> audit)
{
    internal async Task<ControllerReply> HandleAsync(Guid requestId, JsonElement proposal, CancellationToken token)
    {
        audit(requestId, "ModelProposal", new { proposal });
        JsonProperty field;
        ReadCapability capability;
        try
        {
            field = ChatApp.ValidateProposal(proposal);
            if (field.Name is "answer" or "refusal")
                return new ControllerReply(field.Name, field.Value.GetString()!);
            capability = ChatApp.Authorize(settings, field.Value.GetString()!);
        }
        catch (InvalidOperationException)
        {
            audit(requestId, "AuthorizationDenied", new { reason = "InvalidProposalOrUnapprovedCapability", dispatched = false });
            throw;
        }

        var executionId = Guid.NewGuid();
        var observedHash = await currentDefinitionHash(capability, token);
        if (observedHash != capability.DefinitionHash)
        {
            audit(requestId, "AuthorizationDenied", new { executionId, capabilityId = capability.Id,
                reason = "DefinitionChangedOrUnavailable", observedHash, dispatched = false });
            throw new InvalidOperationException("Tool definition changed or is unavailable. Rediscover and review.");
        }
        audit(requestId, "AuthorizationAllowed", new { executionId, capability });
        // Persist the intent before crossing the external boundary. A final DispatchStarted
        // record without a result means the outcome requires investigation, never automatic replay.
        audit(requestId, "DispatchStarted", new { executionId, capabilityId = capability.Id });
        JsonElement result;
        try { result = await invoke(capability, token); }
        catch (Exception)
        {
            audit(requestId, "ExecutionUnknown", new { executionId, capabilityId = capability.Id,
                reason = "InvocationDidNotReturnAResult" });
            throw;
        }
        // Persist complete evidence before returning anything to the model or summary calculator.
        audit(requestId, "ExecutionResult", new { executionId, capabilityId = capability.Id,
            resultHash = ChatApp.Hash(result), result });
        return new ControllerReply("result", capability.Id, result);
    }
}
