# Model and thinking-level indicator

This is the shared visual vocabulary for the taskboard `model-level-indicator`
and the coding-agent-chat sibling `chat-model-level-indicator`. Both consumers
must use the same codes and host-provided CSS custom properties so a model keeps
the same identity between a board card, task detail, and a chat turn.

## Chosen form

Use one compact capsule with two adjacent text segments:

`MODEL | level`

The model segment uses a stable abbreviation and a model-family colour. The
level segment uses a lowercase abbreviation. Colour is never the only channel.
Do not add a provider icon to this compact form: icons cost horizontal space and
identify the CLI, not the model family. Do not encode the level only as dots:
dots make magnitude easy to compare but make named levels such as `xhigh` and
`ultra` hard to recover.

The capsule stays at `--studio-card-pill-height`. A task inherited from client
defaults uses a dashed outline. A quota fallback uses a uniform warning ring.
Neither state changes card height.

## Model-family contract

| Match | Code examples | Shared foreground token |
|---|---|---|
| `gpt-*-sol` | `SOL` | `--studio-model-sol` |
| `gpt-*-luna` | `LUN` | `--studio-model-openai` |
| `gpt-*-ter` | `TER` | `--studio-model-ter` |
| Claude Opus | `OP4.8` | `--studio-model-opus` |
| Claude Sonnet | `SON5`, `SON4.6` | `--studio-model-sonnet` |
| Claude Haiku | `HAI4.5` | `--studio-model-haiku` |
| Gemini | `GEM2.5P`, `GEM2.5F` | `--studio-model-gemini` |
| Other GPT/OpenAI | `GPT5.5`, `COD5`, `GPT5.4M` | `--studio-model-openai` |
| Human assignment | `HUM` | `--studio-model-human` |
| Unknown model | best-effort code, maximum seven characters | `--studio-model-unknown` |

Family colours are semantic tokens, not status colours. Components derive a
low-alpha background and border from the foreground token. The dark and light
themes provide different pigment values through
`frontend/src/styles/_tokens-semantic.scss`.

## Thinking-level contract

| Full value | Code |
|---|---|
| `minimal` | `min` |
| `low` | `l` |
| `medium` | `m` |
| `high` | `h` |
| `xhigh` | `xh` |
| `ultra` | `u` |
| `max` | `max` |

The effective level wins over configured and default values. When it differs
from the client default, strengthen the level segment while retaining the same
family hue. The tooltip explains configured/default differences.
When live discovery does not offer a pinned `ultra` level but does offer
`xhigh`, show `xh*` and explain the mapping in the tooltip. A model that offers
`ultra`, including GPT-6 Sol, keeps `u` without a mapping marker.

## Tooltip and accessibility

The compact capsule always exposes the complete model ID, full thinking-level
name, and CLI name. Board tooltips may additionally explain the owner default,
effective run metadata, and fallback reason. The accessible label carries the
same three primary fields, so the abbreviated surface is not the only source of
information.

The selected option was compared with a dot-scale variant and an icon/size
variant in `results/model-level-indicator-variants.html` and its captured PNG.
