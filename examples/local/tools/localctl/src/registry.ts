// Registry resolution: a `name:tag` image entry in the roles file becomes a
// `name@sha256:…` digest through `docker buildx imagetools inspect`, so a
// rendered bundle NEVER rides a mutable tag. Registry-unreachable keeps the
// last locked digest and warns (sim never hits this: build-local images).
import { fail, run } from "./util.js";
import type { ImageKey, Lockfile } from "./lock.js";

export const DIGEST_RE = /^[a-z0-9.\-\/:]+@sha256:[0-9a-f]{64}$/;
export const TAG_RE = /^[a-z0-9.\-\/:]+:[A-Za-z0-9._-]+$/;

export async function resolveTagToDigest(ref: string): Promise<string> {
  const r = await run(["docker", "buildx", "imagetools", "inspect", "--format", "{{.Digest}}", ref]);
  if (r.code !== 0) fail(`imagetools inspect ${ref}: ${r.stderr.trim()}`);
  const digest = r.stdout.trim();
  if (!/^sha256:[0-9a-f]{64}$/.test(digest)) fail(`imagetools inspect ${ref}: unexpected digest "${digest}"`);
  const repo = ref.slice(0, ref.lastIndexOf(":"));
  return `${repo}@${digest}`;
}

/** Resolve one roles `images:` entry to what the bundle will render.
 * force re-resolves tags even when a lock digest exists (upgrade). */
export async function resolveImage(
  key: ImageKey,
  ref: string,
  localTag: string,
  lock: Lockfile,
  force: boolean,
): Promise<string> {
  if (ref === "local") return localTag;
  if (DIGEST_RE.test(ref)) return ref;
  if (!TAG_RE.test(ref)) fail(`images.${key}:? "${ref}" is neither a digest nor a name:tag`);
  try {
    return await resolveTagToDigest(ref);
  } catch (err) {
    const locked = lock.resolved[key];
    if (locked !== undefined && !force) {
      console.warn(`warn: registry unreachable for ${ref}; keeping locked ${locked}`);
      return locked;
    }
    throw err;
  }
}
