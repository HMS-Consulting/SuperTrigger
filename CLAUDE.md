# Claude Code Performance & Efficiency Rules

## Subagent Management
- **Minimize Spawning:** Do NOT spawn subagents unless explicitly requested or for highly complex, parallel task execution.
- **Single-Context Execution:** Perform file edits, git operations, and terminal commands directly in the main context whenever possible.
- **Search Efficiency:** For file search or exploration, use targeted shell commands (`grep`, `find`, `rg`) directly instead of spawning an Explore subagent.

## Context & Token Optimization
- **Concise Responses:** Keep explanations minimal. Focus strictly on code changes, terminal outputs, and direct answers.
- **Targeted Reading:** Do not read entire folders or large documentation files. Read only the specific lines or files needed for the task.
- **No Unsolicited Audits:** Do not review or refactor unrequested code unless directly relevant to the current bug or feature.

## Task Workflow
- **Clear Boundaries:** Complete the requested task and stop. Do not proactively suggest or initiate follow-up implementations.
- **Context Refresh Prompt:** Prompt the user to run `/compact` or `/clear` if a single session grows beyond 10-15 message turns.