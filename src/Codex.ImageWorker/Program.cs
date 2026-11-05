using Codex.Persistence;

// stdin and stdout carry the requests and replies of BookImageWire; nothing else is ever written to stdout.
return await BookImageWorker.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
