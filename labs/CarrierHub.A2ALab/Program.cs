using A2A;
using Microsoft.Agents.AI;

// "Another team's agent": it knows only CarrierHub's address. Everything else it learns from the agent card.
// It references NO CarrierHub project: A2A is the only thing the two sides share.
//   dotnet run --project labs/CarrierHub.A2ALab        (the AppHost must be running)

var host = new Uri(args.ElementAtOrDefault(0) ?? "https://localhost:7243");

// 1. Discover: GET /.well-known/agent-card.json, then build an AIAgent that talks A2A to the address in the card.
var resolver = new A2ACardResolver(host);
AIAgent agent = await resolver.GetAIAgentAsync();
Console.WriteLine($"Discovered: {agent.Name}");
Console.WriteLine($"            {agent.Description}");

// 2. One conversation: the remote agent is used like any local AIAgent.
var session = await agent.CreateSessionAsync();

string[] questions =
[
    "What does NordPost pay for a late parcel?",          // its job
    "And FastShip?",                                      // a follow-up: does the conversation carry over A2A?
    "Where is my order ORDER-1008?",                      // merchant data: it must not be able to reach it
    "Open a damage claim for ORDER-1002.",                // an action: it has no such tool
    "What's the weather in Ankara?",                      // out of scope
];

foreach (var question in questions)
{
    Console.WriteLine($"\n> {question}");
    Console.WriteLine(await agent.RunAsync(question, session));
}