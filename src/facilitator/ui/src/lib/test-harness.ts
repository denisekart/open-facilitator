// Vitest setup: jsdom lacks the IO plumbing Svelte Flow measures with.
// One minimal stub so canvases mount (layout correctness is proven
// separately, pure, in flow-layout.test.ts).

class ResizeObserverStub {
  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
}

class IntersectionObserverStub {
  root = null;
  rootMargin = "";
  thresholds: number[] = [];
  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
  takeRecords(): [] {
    return [];
  }
}

globalThis.ResizeObserver ??= ResizeObserverStub as unknown as typeof ResizeObserver;
globalThis.IntersectionObserver ??=
  IntersectionObserverStub as unknown as typeof IntersectionObserver;

// Svelte Flow's store builds a MediaQuery for prefers-color-scheme; jsdom
// ships no matchMedia. One fixed light-mode stub is enough.
window.matchMedia ??= ((query: string): MediaQueryList => ({
  matches: false,
  media: query,
  onchange: null,
  addListener: () => undefined,
  removeListener: () => undefined,
  addEventListener: () => undefined,
  removeEventListener: () => undefined,
  dispatchEvent: () => false,
})) as typeof window.matchMedia;

if (!("DOMPoint" in globalThis)) {
  class DOMPointStub {
    x: number;
    y: number;
    z: number;
    w: number;
    constructor(x = 0, y = 0, z = 0, w = 1) {
      this.x = x;
      this.y = y;
      this.z = z;
      this.w = w;
    }
  }
  globalThis.DOMPoint = DOMPointStub as unknown as typeof DOMPoint;
}

if (!("DOMMatrix" in globalThis)) {
  class DOMMatrixStub {
    static fromMatrix(): DOMMatrixStub {
      return new DOMMatrixStub();
    }
    a = 1;
    b = 0;
    c = 0;
    d = 1;
    e = 0;
    f = 0;
    is2D = true;
    isIdentity = true;
    multiplySelf(): DOMMatrixStub {
      return this;
    }
    translateSelf(): DOMMatrixStub {
      return this;
    }
    scaleSelf(): DOMMatrixStub {
      return this;
    }
    inverse(): DOMMatrixStub {
      return this;
    }
    transformPoint(p?: { x: number; y: number }): { x: number; y: number } {
      return { x: p?.x ?? 0, y: p?.y ?? 0 };
    }
  }
  globalThis.DOMMatrix = DOMMatrixStub as unknown as typeof DOMMatrix;
}

if (!Element.prototype.scrollIntoView) {
  Element.prototype.scrollIntoView = () => undefined;
}

// CodeMirror's measure loop asks text ranges for client rects; jsdom's
// Range has none. Zero rects simply means "nothing measured" — layout
// correctness is proven in the browser, not here.
if (!Range.prototype.getClientRects) {
  Range.prototype.getClientRects = () =>
    Object.assign([], { item: () => null }) as unknown as DOMRectList;
}
if (!Range.prototype.getBoundingClientRect) {
  Range.prototype.getBoundingClientRect = () => new DOMRect(0, 0, 0, 0);
}
