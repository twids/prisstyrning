---
description: 'Fetch PR review comments, fix the code issues, reply to each comment, resolve threads, commit, and push.'
mode: 'agent'
tools: ['execute/runInTerminal', 'edit/editFiles', 'read/readFile', 'search/textSearch', 'search/fileSearch', 'todo']
---

# Resolve PR Review Comments

You are tasked with processing and resolving all outstanding review comments on the current branch's pull request. Follow this workflow exactly:

## Step 1: Identify the PR

Run `gh pr view --json number,title,url` to find the PR for the current branch. If no PR exists, inform the user and stop.

## Step 2: Fetch Review Comments

1. Fetch inline review comments:
   ```
   gh api repos/{owner}/{repo}/pulls/{number}/comments --jq '.[] | select(.in_reply_to_id == null) | {id: .id, path: .path, line: .line, body: .body}'
   ```
2. Fetch review thread resolution status via GraphQL:
   ```
   gh api graphql -f query='{ repository(owner:"{owner}", name:"{repo}") { pullRequest(number:{number}) { reviewThreads(first:50) { nodes { id isResolved comments(first:1) { nodes { body path } } } } } } }'
   ```
3. Filter to only **unresolved** threads. If all threads are already resolved, inform the user and stop.

## Step 3: Address Each Comment

For each unresolved review comment:
1. **Read the relevant file** at the referenced line to understand the context
2. **Apply the fix** described in the comment (code change, refactor, or improvement)
3. **Verify** the fix compiles (run type-checking or build as appropriate for the project)

## Step 4: Reply and Resolve

For each addressed comment:
1. **Reply** to the comment thread confirming the fix:
   ```
   gh api repos/{owner}/{repo}/pulls/{number}/comments/{comment_id}/replies -f body="Fixed: {brief description of what was done}"
   ```
2. **Resolve** the thread via GraphQL mutation:
   ```
   gh api graphql -f query='mutation { resolveReviewThread(input:{threadId:"{thread_node_id}"}) { thread { isResolved } } }'
   ```

## Step 5: Commit and Push

1. Stage only the modified files
2. Commit with message: `fix: address PR review feedback\n\n- {bullet for each fix}`
3. Push to the current branch

## Important Notes

- On Windows/PowerShell, use variables to avoid quote-escaping issues with GraphQL queries:
  ```powershell
  $q = '{ repository(owner:\"owner\", name:\"repo\") { ... } }'
  gh api graphql -f "query=$q"
  ```
- Only address comments that are actionable code review feedback (skip bot-generated coverage reports, etc.)
- If a comment suggests a change you disagree with or that would break functionality, reply explaining why and still resolve the thread
- Run the project's build/typecheck after all fixes to ensure nothing is broken
