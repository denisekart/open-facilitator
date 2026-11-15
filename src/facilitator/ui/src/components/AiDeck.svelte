<script lang="ts">
  import { X } from "lucide-svelte";
  import { deck } from "../lib/deck.svelte.js";
  import ChatView from "./ChatView.svelte";

  // The AiDeck is chrome, not a screen: it opens over every category from
  // the rail (or a #/chat deep link). Desktop: NON-MODAL companion panel —
  // the console underneath stays clickable and undimmed, because the deck
  // is briefed ON the screen you are working on; closing is the rail
  // toggle, the close button, or Esc (unless focus sits in a text field,
  // where Esc belongs to the field). Mobile (≤720px): a modal bottom
  // sheet — there is no meaningful "underneath" at that width — with a
  // scrim that dismisses on tap.
  let { onclose }: { onclose: () => void } = $props();

  let modal = $state(false);
  let media: MediaQueryList | undefined;

  $effect(() => {
    // jsdom (vitest) has no matchMedia: absent means "desktop, non-modal".
    if (typeof window.matchMedia !== "function") {
      modal = false;
      return;
    }
    media = window.matchMedia("(max-width: 720px)");
    modal = media.matches;
    const onChange = (ev: MediaQueryListEvent): void => {
      modal = ev.matches;
    };
    media.addEventListener("change", onChange);
    return () => media?.removeEventListener("change", onChange);
  });

  function onKeydown(ev: KeyboardEvent): void {
    if (ev.key !== "Escape" || !deck.state.open) {
      return;
    }
    // Esc inside a text field is the field's own business on desktop; the
    // mobile sheet is a dialog, so Esc always closes there.
    const typing =
      ev.target instanceof HTMLElement &&
      ["INPUT", "TEXTAREA", "SELECT"].includes(ev.target.tagName);
    if (typing && !modal) {
      return;
    }
    ev.stopPropagation();
    onclose();
  }
</script>

<svelte:window onkeydown={onKeydown} />

{#if deck.state.open}
  <div
    class="fixed inset-0 z-40 flex justify-end {modal ? 'bg-black/20 items-end' : 'pointer-events-none items-stretch'}"
    role="dialog"
    aria-modal={modal}
    aria-label="AiDeck"
    data-testid="ai-deck">
    {#if modal}
      <button
        class="absolute inset-0 cursor-default"
        aria-label="close AiDeck"
        data-testid="ai-deck-scrim"
        onclick={onclose}></button>
    {/if}
    <div
      class="pointer-events-auto relative flex h-full w-[min(560px,100%)] flex-col border-l border-edge bg-canvas shadow-2xl {modal
        ? 'h-[85vh] w-full border-t border-l-0'
        : ''}">
      <div class="flex items-center justify-between border-b border-edge px-4 py-2">
        <span class="text-sm font-semibold">AiDeck</span>
        <button class="btn" data-testid="ai-deck-close" onclick={onclose}>
          <X class="size-4" strokeWidth={1.5} aria-hidden="true" />close
        </button>
      </div>
      {#if deck.available().length > 0}
        <div class="flex flex-wrap gap-1 border-b border-edge px-4 py-2" data-testid="ai-deck-screen-actions">
          {#each deck.available() as action (action.id)}
            <button class="chip" data-testid={`deck-action-${action.id}`} onclick={action.run}>
              {action.label}
            </button>
          {/each}
        </div>
      {/if}
      <div class="min-h-0 grow overflow-y-auto p-3">
        <ChatView screenContext={deck.context} initialSession={deck.state.sessionId} />
      </div>
    </div>
  </div>
{/if}
