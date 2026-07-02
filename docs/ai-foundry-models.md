# Models available on `cor-ais-cap-dev-we-001`

Snapshot taken 2026-07-02 against subscription `cor-cap-dev` (`b61f3453-5d67-4125-b9dd-ff5458c590bf`),
region `westeurope`, via `az cognitiveservices account list-models` / `az cognitiveservices usage list`.
Quota is subscription+region wide (shared across all Cognitive Services accounts in `westeurope`), not
per-account. Re-run the commands below before relying on this for a deployment decision - deprecation
and quota both move over time.

## OpenAI models (relevant to this project)

`New deployments` is only marked "Blocked (confirmed)" where we actually hit the error; "Deprecating"
lifecycle alone does **not** reliably mean new deployments are blocked - `gpt-4o` is `Deprecating` and we
deployed it successfully, `gpt-4.1` is also `Deprecating` and was hard-blocked. Untested ones are marked
accordingly.

| Model | Version | Lifecycle | New deployments | Retirement (inference cutoff) | GlobalStandard quota used / limit (K TPM) |
|---|---|---|---|---|---|
| gpt-5.1 | 2025-11-13 | GenerallyAvailable | Allowed (recommended replacement for gpt-4.1) | 2027-05-15 | 0 / 1000 |
| gpt-5 | 2025-08-07 | GenerallyAvailable | Allowed | 2027-02-06 | 0 / 1000 |
| gpt-5-mini | 2025-08-07 | GenerallyAvailable | Allowed | 2027-02-06 | 0 / 1000 |
| gpt-5-nano | 2025-08-07 | GenerallyAvailable | Allowed | 2027-02-06 | 0 / 5000 |
| gpt-5.2 | 2025-12-11 | GenerallyAvailable | Allowed | 2026-12-12 | 0 / 1000 |
| gpt-5.4 | 2026-03-05 | GenerallyAvailable | Allowed | 2027-03-05 | 0 / 1000 |
| gpt-5.5 | 2026-04-24 | GenerallyAvailable | **0 quota available** - needs a quota request first | 2027-04-24 | 0 / 0 |
| gpt-4o | 2024-11-20 | Deprecating | Confirmed allowed (our `evaluation` deployment uses it) | 2026-10-01 | 60 / 450 |
| gpt-4o-mini | 2024-07-18 | Deprecating | Untested | 2026-10-01 | 0 / 2000 |
| gpt-4.1 | 2025-04-14 | Deprecating | **Blocked (confirmed)** - `ServiceModelDeprecating`, despite 1000 K TPM quota still reserved | 2026-10-14 | 0 / 1000 |
| gpt-4.1-mini | 2025-04-14 | Deprecating | Untested, likely blocked too | 2026-10-14 | 0 / 5000 |
| gpt-4.1-nano | 2025-04-14 | Deprecating | Untested, likely blocked too | 2026-10-14 | 0 / 5000 |
| o1 | 2024-12-17 | Deprecating | Untested - retires in ~2 weeks | 2026-07-15 | 0 / 500 |
| o3-mini | 2025-01-31 | Deprecating | Untested | 2026-08-02 | 0 / 500 |
| o4-mini | 2025-04-16 | Deprecating | Untested - has quota, but so did gpt-4.1 before it got blocked; don't trust quota presence as a deployability signal for `Deprecating` models | 2026-10-16 | 0 / 1000 |
| text-embedding-3-large | 1 | GenerallyAvailable | Confirmed allowed (our `embedding` deployment uses it) | 2027-04-15 | 350 / 1000 |
| text-embedding-3-small | 1 | GenerallyAvailable | Untested, no reason to expect issues | 2027-04-15 | 0 / 1000 |
| text-embedding-ada-002 | 2 | GenerallyAvailable | Untested, no reason to expect issues | 2027-04-15 | 0 / 1000 |
| whisper | 001 | GenerallyAvailable | Untested, no reason to expect issues | 2026-12-15 | RPM-based: 0 / 3 (not TPM) |

**Applied in `ai_deployments.tf`**: `querying`, `extraction`, and `evaluation` all moved to `gpt-5.4`
(`2026-03-05`) - the newest **GA** flagship with quota actually available (1000 K TPM, unused).
`gpt-5.5` is newer but has 0 quota in this subscription/region, so it isn't usable yet. Regional
`Standard` SKU was checked too (see the query in "Regenerating this snapshot") - it doesn't offer
`gpt-5.x`/`gpt-4o`/`text-embedding-3-large` at all, only legacy models plus `gpt-4.1-mini`, so
`GlobalStandard` remains the right tier here.

Note: using the same model (`gpt-5.4`) for both generation and evaluation risks self-preference bias in
the eval scores. Worth reconsidering once you're relying on eval numbers for real decisions - a distinct
judge model would be more trustworthy.

## Other models in the catalog (not TPM-quota-based, not currently used by this project)

These are available on the account via the broader Azure AI Foundry model catalog, but use different
quota mechanics (serverless PayGo, provisioned throughput units, or per-resource counts) rather than the
K-TPM quota shown above, so they're listed for awareness only - no token-limit table for these unless we
actually plan to use one, in which case we can pull its specific quota the same way.

- **Meta**: Llama-3.3-70B-Instruct, Llama-4-Maverick-17B-128E-Instruct-FP8, Llama-4-Scout-17B-16E-Instruct, Llama-3.2-11B/90B-Vision-Instruct, Meta-Llama-3.1-405B/8B-Instruct
- **Cohere**: Cohere-command-a-plus-05-2026, Cohere-command-r-08-2024, Cohere-command-r-plus-08-2024, Cohere-embed-v3-english/multilingual, Cohere-rerank-v4.0-fast/pro, cohere-command-a, embed-v-4-0
- **DeepSeek**: DeepSeek-R1, DeepSeek-R1-0528, DeepSeek-V3-0324, DeepSeek-V3.1, DeepSeek-V3.2(-Speciale), DeepSeek-V4-Flash, DeepSeek-V4-Pro
- **Mistral**: Mistral-Large-3, Mistral-large, Ministral-3B, mistral-medium-2505, mistral-medium-3-5, mistral-small-2503, mistral-document-ai-2505/2512, mistral-ocr-4-0, Codestral-2501
- **xAI**: grok-3, grok-3-mini, grok-4-1-fast-non-reasoning/reasoning, grok-4-20-non-reasoning/reasoning, grok-4-fast-non-reasoning/reasoning, grok-4.3
- **Microsoft Phi**: Phi-4, Phi-4-mini-instruct, Phi-4-mini-reasoning, Phi-4-multimodal-instruct, Phi-4-reasoning
- **Image generation**: FLUX-1.1-pro, FLUX.1-Kontext-pro, FLUX.2-pro, MAI-Image-2, MAI-Image-2.5(-Flash), MAI-Image-2e
- **Other**: Kimi-K2.5/2.6/2.7-Code, qwen3-32b, gpt-oss-120b, gpt-oss-20b

## Regenerating this snapshot

```bash
az cognitiveservices account list-models --name cor-ais-cap-dev-we-001 --resource-group cor-cap-ai-dev-we-001 -o table
az cognitiveservices usage list --location westeurope -o table
```
