// The composition root for `curia-mcp`. Stage 2 of the MCP adapter plan builds the stdio host and
// the two anonymous read tools on top of this; today it exists so the architecture scan covers the
// assembly from the moment the directory does. BannedApiTests derives its assembly list from
// src/**/*.csproj and fails by name for any assembly missing from its output, which is what makes
// a new project impossible to add unscanned.
return 0;
