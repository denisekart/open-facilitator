<script lang="ts">
  import { Maximize2, Minimize2, Sparkles, X } from "lucide-svelte";
  import { deck } from "../lib/deck.svelte.js";
  import ChatView from "./ChatView.svelte";

  // The AiDeck is chrome, not a screen: it opens over every category from
  // the rail (or a #/chat deep link). Desktop: NON-MODAL companion panel
  // (docked) or expanded foreground chat window — the console underneath
  // stays clickable and undimmed in docked mode, because the deck is briefed
  // ON the screen you are working on; closing is the rail toggle, close button,
  // or Esc (unless focus sits in a text field, where Esc belongs to the field).
  // Mobile (≤720px): a modal bottom sheet with a scrim that dismisses on tap.
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
    class="fixed inset-0 z-40 flex {modal
      ? 'scrim items-end justify-center'
      : deck.state.expanded
        ? 'pointer-events-none items-center justify-center p-4 md:p-6'
        : 'pointer-events-none items-stretch justify-end'}"
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
      class="deck-panel pointer-events-auto relative flex flex-col bg-surface shadow-overlay {modal
        ? 'h-[82vh] w-full border-t border-edge rounded-t-lg'
        : deck.state.expanded
          ? 'h-[88vh] max-h-[920px] w-[min(1040px,94vw)] rounded-lg border border-edge'
          : 'h-full w-[min(440px,100%)] border-l border-edge'}">
      <div class="flex items-center justify-between border-b border-edge px-3.5 py-2">
        <div class="flex items-center gap-2 min-w-0">
          <div class="flex size-6 items-center justify-center rounded bg-accent-veil border border-border-primary text-accent">
            <Sparkles class="size-3.5" strokeWidth={1.5} aria-hidden="true" />
          </div>
          <span class="text-sm font-semibold tracking-tight text-ink">AiDeck</span>
          {#if deck.context}
            <span
              class="badge bg-accent-veil text-accent border border-border-primary max-w-40 sm:max-w-64 truncate text-[11px] font-medium"
              title={`Briefed on: ${deck.context.label} (${deck.context.route})`}>
              {deck.context.label}
            </span>
          {/if}
        </div>
        <div class="flex items-center gap-1">
          {#if !modal}
            <button
              class="btn px-2"
              data-testid="ai-deck-expand"
              aria-label={deck.state.expanded ? "dock to side" : "expand to foreground"}
              title={deck.state.expanded ? "Dock to side companion panel" : "Expand to foreground chat window"}
              onclick={() => deck.toggleExpanded()}>
              {#if deck.state.expanded}
                <Minimize2 class="size-4" strokeWidth={1.5} aria-hidden="true" />
                <span class="max-sm:hidden">dock</span>
              {:else}
                <Maximize2 class="size-4" strokeWidth={1.5} aria-hidden="true" />
                <span class="max-sm:hidden">expand</span>
              {/if}
            </button>
          {/if}
          <button class="btn px-2" data-testid="ai-deck-close" onclick={onclose}>
            <X class="size-4" strokeWidth={1.5} aria-hidden="true" />close
          </button>
        </div>
      </div>

      {#if deck.available().length > 0}
        <div
          class="flex flex-wrap items-center gap-1.5 border-b border-edge bg-surface-alt px-3.5 py-1.5"
          data-testid="ai-deck-screen-actions">
          <span class="text-[11px] font-semibold text-ink-2 uppercase tracking-wider">Page actions:</span>
          {#each deck.available() as action (action.id)}
            <button class="chip chip-active text-xs" data-testid={`deck-action-${action.id}`} onclick={action.run}>
              {action.label}
            </button>
          {/each}
        </div>
      {/if}

      <div class="min-h-0 grow overflow-hidden flex flex-col p-2 sm:p-3">
        <ChatView
          screenContext={deck.context}
          initialSession={deck.state.sessionId}
          expanded={deck.state.expanded && !modal} />
      </div>
    </div>
  </div>
{/if}

<style>
  /* A drawer takes over a region on purpose (§13): the one place sliding
     belongs — 180ms in, 120ms out, standard easing. Desktop keeps it
     non-modal: content stays interactive beside it, so no scrim there. */
  @media (prefers-reduced-motion: no-preference) {
    .deck-panel {
      transition: transform 180ms cubic-bezier(0.2, 0, 0, 1),
                  width 180ms cubic-bezier(0.2, 0, 0, 1),
                  height 180ms cubic-bezier(0.2, 0, 0, 1);
    }
  }
</style>
