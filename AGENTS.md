# Agent Configuration

## Plan Directory
- Use `plans/` for all Atlas/Prometheus plan files.

## Custom Agents (Repo-local)
- Agent definitions are stored in `.github/agents/`.
- Included agents:
  - `Atlas.agent.md`
  - `Prometheus.agent.md`
  - `Oracle-subagent.agent.md`
  - `Explorer-subagent.agent.md`
  - `Sisyphus-subagent.agent.md`
  - `Code-Review-subagent.agent.md`
  - `Frontend-Engineer-subagent.agent.md`

## Installation
- Run `./scripts/install-atlas-agents.sh` from the repository root to install/symlink these into your VS Code prompts directory.
