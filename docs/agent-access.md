# Agent access

After scanning, open **More → Agent access** at the folder you want help with. Add any excluded locations, choose an access level, then **Start sharing**. Expand **MCP connection setup** and copy it into your agent client's MCP configuration. The normal Space Maid executable provides the stdio bridge; no server installation, network listener or administrator permission is needed. Close the window or choose **Stop sharing** to revoke access. Sharing starts off each time and is never saved.

| Level | What the agent can do |
| --- | --- |
| Summary only | Read scoped file-name/folder counts, observed logical/allocated sizes and numeric coverage flags. |
| Aggregate groups | Also compare coarse tool, storage and host-policy classes using session-only random group IDs. |
| Suggest groups for review | Also queue a suggestion of up to eight reviewable groups with an explicitly unverified note. |

There is no level that grants filenames, paths, timestamps, file contents, transcripts, photo metadata, credentials, native file IDs, arbitrary searches or commands. Rule titles, extension prose, warning messages and exception messages never enter responses. Unknown extension IDs become `Other tools`. All levels operate on the existing immutable metadata scan. Agents cannot change scope, exclusions, access level, optimization consent or retention settings. They cannot scan new locations, request elevation, empty the Recycle Bin, select cleanup targets or execute cleanup.

Excluded items are omitted from the summary. Findings inside an exclusion or containing one are omitted from groups; an ancestor's size would otherwise disclose excluded data. Classification and size are observations, not proof of safe disposal. Group sizes collapse nested targets within each group, but **different groups can overlap and must not be added**. Unknown allocation, shared names, missing streams, incomplete volume links, retained metadata and classification failures stay explicit. Recovery is never promised.

The metadata summary has the existing host budget of 20,000 visited entries. A larger scope returns partial totals with `traversalComplete: false`; narrow the scanned scope for complete evidence. Incomplete coverage or missing cleanup classifications disables review suggestions. Protected tool-managed stores are visible as aggregates but cannot be suggested. Agent history and worktrees remain manual-review storage.

## Human review and evaluating results

`suggest_review` queues one pending suggestion and does not open a review window or change any selection. The person using Space Maid chooses **Review suggested folders…**. The connection stops before review begins. Only the host's already discovered findings are mapped back to exact locations; existing live identity, rule, Git, activity, retention and cleanup safeguards run normally. The person sees the exact paths in the normal review window and can keep them. No agent statement creates a deletion target. Existing unrelated selections are restored using the shared review workflow.

After reviewing or cleaning, refresh the scan and start a new session to compare aggregate results. Earlier IDs never transfer. A replaced/stale scan, host work in progress or application shutdown refuses further data requests. A refused session cannot recover its old authority if the host later returns to an idle state. Closing the access window also disconnects idle bridges. A session accepts at most four suggestions, each with a 500-character plain-text note, and one at a time.

## Integration

This uses the [MCP stdio transport](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports), [initialization lifecycle](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle) and [tool discovery/calls](https://modelcontextprotocol.io/specification/2025-11-25/server/tools), protocol version `2025-11-25`. Supported methods are `initialize`, `notifications/initialized`, `ping`, `tools/list` and `tools/call`. Tool results contain both JSON text and `structuredContent`. Available tools depend on the session's level:

- `get_summary`, arguments `{}`.
- `get_groups`, arguments `{}`.
- `suggest_review`, arguments `{"groupIds":["issued-guid"],"note":"Unverified reason to review"}`.

No resources or prompts are advertised. Paths, extra/duplicate fields, invented/foreign IDs, blocked groups and elevated actions are refused. Requests are bounded to 16,384 characters and JSON depth 16. The local connection permits one client at a time, requires a fresh 256-bit secret and a same-user Windows named pipe, limits authentication to five seconds, and closes idle connections after ten minutes. Reconnect using the same setup while the host session remains valid. Each connection is limited to 512 frames. The configuration's environment secret is not a tool argument or protocol response; keep that setup private. No requests, model notes or connection credentials are persisted by this feature.

**Privacy boundary:** aggregates still reveal sizes, counts and coarse tool usage; choose scope and exclusions accordingly. This connector limits information obtained *through Space Maid*. It does not sandbox an independently running external agent or revoke filesystem, shell, browser, connector or other capabilities that agent already has. For actual content isolation, run the external worker in its own restricted environment with only this connector. Space Maid's separate [local assistant](local-assistant.md) retains its existing isolated-worker behavior and its own path-bearing evidence interface; it is not this aggregate connection.

Verification uses synthetic scan metadata, owned pipe/process fixtures and a focused WPF interaction/render check. It covers projection privacy, exclusions, overlap accounting, capability restrictions, suggestion validation, protocol rejection, revocation, actual stdio transport and human-triggered review routing. These checks do not prove disposal safety for personal data or compatibility with every external MCP client; no personal cleanup is performed by verification.

The isolated Release build passed with zero warnings/errors. All 21 targeted checks passed: 11 aggregate/protocol tests, seven existing review-boundary tests, two real Windows transport tests, and one WPF interaction test. Light/dark and compact renders were inspected. The actual subprocess check includes authentication, an aggregate tool call and idle termination after revocation; rejected credentials also leave the next connection usable.
