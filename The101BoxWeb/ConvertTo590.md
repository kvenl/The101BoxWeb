# Converting The101BoxWeb to another radio (Kenwood TS-590SG)

## Start from THIS web app as a template

Copy this project and adapt it for the other radio.

### What you get for free
- All the **infrastructure is done**: WebSocket, serial port engine, poll loop, broadcast state, HTML/CSS framework, slider controls, button system, status bar, self-contained publish setup
- The **architecture is proven**: the pattern of `RadioState` → `RadioEngine` → `applyState()` in JS is clean and reusable
- All the **lessons learned** are baked in: error handling, reconnect logic, debounced sliders, mouse wheel, self-contained deployment...

### What you'd change

| Part | Effort |
|---|---|
| `RadioState` class | Replace properties with the other radio's parameters |
| `BuildPollCmds()` | Replace CAT commands with the other radio's protocol |
| `ProcessResponse()` | Replace the CAT response parser |
| `HandleClientMessage()` | Adapt special commands |
| HTML canvas layout | Rebuild based on the other desktop app's layout |
| JS `applyState()` | Wire up the new buttons/sliders |

### Workflow suggestion
1. **Copy** this project to a new folder/repository
2. Open the **other desktop app** side-by-side for layout reference (`Form1.Designer.cs` gives exact pixel positions)
3. Work in the **copy of this web app** replacing only the radio-specific parts
4. Keep the infrastructure (`RadioEngine` skeleton, WebSocket plumbing, CSS framework) intact

> The desktop app is only useful as a **reference for layout and CAT commands** — not as a starting point for the web app code itself.
