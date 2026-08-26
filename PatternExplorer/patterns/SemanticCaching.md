---
{
  "title": "Semantic Caching",
  "summary": "Reuse a cached answer when the new question means the same thing, not just when it matches.",
  "category": "Knowledge & state",
  "projects": [ { "flavor": "AgentFramework", "path": "SemanticCaching.AgentFramework" } ]
}
---

## What it is

An ordinary response cache keys on the exact request text, so *"What is the capital of
France?"* and *"Which city is the capital of France?"* are two different keys and two paid
model calls. Semantic caching keys on *meaning*: embed the incoming question, compare it to the
embeddings of questions already answered, and if the closest one is similar enough, return its
stored response.

The two layers stack. Exact-match caching is a free hash lookup, so it goes outermost; the
semantic layer costs one embedding call, so it goes underneath; the model is the last resort.

## When to use it

- Many users ask the same handful of questions in slightly different words — support, FAQ,
  docs assistants.
- Latency or cost per call matters more than getting a freshly worded answer every time.

Skip it when answers depend on time, user identity, or conversation state — a cached reply for
"what's my order status" is a bug, not a saving. Also skip it when the threshold would have to
be so high that hits become rare; you would pay for embeddings and get nothing back.

## Isolation: the part that is easy to get wrong

A semantic cache keyed on the conversation alone — text plus a couple of `ChatOptions` fields —
will happily serve tenant A's cached answer to tenant B, or an answer generated under a tool
policy or system prompt that no longer applies. A cache with no identity, no authorization scope,
no tool policy and no data revision in its key is a cross-tenant data leak waiting to happen.

`SemanticCachingChatClient` makes a `CacheNamespace` a required constructor argument, so those
dimensions cannot be forgotten. Every partition key is built from all six fields, plus the prior
turns of the conversation, plus the request options:

- `TenantId` — whose data this is.
- `PrincipalScopeHash` — the authorization scope the caller was granted; a broader or narrower
  scope must not reuse another scope's answer.
- `SystemPromptHash` — which system prompt produced this answer.
- `ToolSchemaHash` — which tools were available; an upgraded or downgraded tool policy is a
  different partition.
- `ModelVersion` — which model produced the answer.
- `DataRevision` — which revision of the underlying knowledge the answer was drawn from; bump it
  when the source data changes so stale answers stop being served.

The prior-turn digest covers every `AIContent` kind in the history — function calls and their
results included, not just message text — so a tool call earlier in the conversation can't be
silently dropped from the key.

Entries also expire (`entryLifetime`) and each partition is bounded (`maxEntriesPerPartition`,
oldest evicted first): an unbounded cache that never forgets is its own kind of leak.

## How the demo works

`SemanticCachingChatClient` is a `DelegatingChatClient` that sits in a `ChatClientBuilder`
pipeline: `UseDistributedCache` (exact match, backed by `MemoryDistributedCache`) wraps the
semantic client, which wraps the real chat client. It embeds the last user message, scans the
in-memory list of `(embedding, response, expiresAt)` entries for its partition with
`TensorPrimitives.CosineSimilarity`, and serves the best match when the score reaches the `0.9`
threshold — high enough to accept close paraphrases while rejecting merely related questions.
Expired entries are dropped on read; the response is cloned both when it's stored and when it's
served, so neither the cache nor a caller can corrupt the other's copy.

Four queries run through a `CachingAgent` with no shared session: a new question, the identical
question, a paraphrase, and an unrelated one. The sample builds one static `CacheNamespace`
(single caller, no tools, one document revision) to keep the demo runnable offline — a real
deployment reads `TenantId` and `PrincipalScopeHash` from the caller's auth context per request.

```mermaid
flowchart LR
    Q[Query] --> X{Exact cache hit?}
    X -->|yes| R[Cached response]
    X -->|no| E[Embed query]
    E --> P[Look up namespace<br/>partition]
    P --> C{Cosine similarity<br/>at least 0.9?}
    C -->|yes| R
    C -->|no| M[Real model call]
    M --> ST[Store embedding + response<br/>with expiry; evict oldest<br/>if over the bound]
    ST --> R
```

The `Hits` / `Misses` / `LastSimilarity` counters on the delegating client are what the program
reads to classify each call.

## Key APIs

- `DelegatingChatClient` — the base class for wrapping an `IChatClient` with your own behaviour.
- `new ChatClientBuilder(inner).UseDistributedCache(...).Use(inner => ...)` — layering caches,
  cheapest check outermost.
- `MemoryDistributedCache` — the built-in exact-match store used for the outer layer.
- `IEmbeddingGenerator.GenerateVectorAsync(query)` — one embedding per uncached question.
- `TensorPrimitives.CosineSimilarity(cached, incoming)` — the similarity scan, an O(n) list
  walk that a persistent vector store would replace in production.
- `CacheNamespace` — the six required isolation dimensions (tenant, authorization scope, system
  prompt, tool schema, model version, data revision) that key every partition.

## What to watch in the output

Each call prints `User (<label>): <query>` then a status line: `[MISS]`, `[HIT(exact)]` or
`[HIT(semantic ~0.95)]`, followed by the elapsed milliseconds. The interesting rows are the
identical question — an exact hit in near-zero time — and the paraphrase, which only the
semantic layer can catch. The run ends with `Summary: 4 queries, 2 LLM calls, 2 saved by
caching.` **Middleware** shows the same `DelegatingChatClient` seam used for logging instead of
caching, and **RAG** uses the same embedding-plus-cosine machinery for retrieval.
