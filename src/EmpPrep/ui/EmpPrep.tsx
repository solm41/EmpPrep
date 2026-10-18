import React from "react";
import { Field, TextInput, TextArea, NumberInput, SelectInput, SaveButton } from "@cove/runtime/components";
import { extensionFetch } from "@cove/runtime/api";

const { useCallback, useEffect, useMemo, useRef, useState } = React;

const API_BASE = "/api/ext/empprep";

// ─────────────────────────── API helpers ───────────────────────────

async function api(path: string, options: RequestInit = {}): Promise<any> {
  const response = await extensionFetch(`${API_BASE}${path}`, {
    ...options,
    headers: { "Content-Type": "application/json", ...(options.headers || {}) },
  });
  const text = await response.text();
  let data: any = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = text;
    }
  }
  if (!response.ok) {
    throw new Error(data?.message || data?.detail || text || response.statusText || "Request failed.");
  }
  return data;
}

const get = (path: string) => api(path);
const post = (path: string, body?: any) =>
  api(path, { method: "POST", body: body === undefined ? undefined : JSON.stringify(body) });
const put = (path: string, body: any) => api(path, { method: "PUT", body: JSON.stringify(body) });
const del = (path: string) => api(path, { method: "DELETE" });

/**
 * Fetch an authenticated binary endpoint as an object URL.
 *
 * Image and download endpoints cannot be used as a bare `src`, because the browser would send that
 * request without the Cove auth header. Fetching through `extensionFetch` and wrapping the blob
 * keeps the credentials handling identical to every other call.
 */
async function objectUrl(path: string): Promise<string> {
  const response = await extensionFetch(`${API_BASE}${path}`);
  if (!response.ok) throw new Error(`Failed to load ${path}`);
  return URL.createObjectURL(await response.blob());
}

// ─────────────────────────── types ───────────────────────────

interface BatchSummary {
  id: string;
  name: string;
  itemCount: number;
  readyCount: number;
  updatedAtUtc: string;
}

interface Screenshot {
  seconds: number;
  path: string;
  kind: "frame" | "cover" | "sheet";
  featured: boolean;
  url?: string | null;
  thumbUrl?: string | null;
  viewerUrl?: string | null;
  fileName: string;
  /** Whether this image is uploaded: the full-size images and the contact sheet. */
  uploads: boolean;
  isFullSize: boolean;
}

interface PrepPackage {
  itemId: string;
  videoId: number;
  status: string;
  releaseName: string;
  title: string;
  postTitle: string;
  /** Fields holding a stored edit rather than the generated value. */
  editedFields: string[];
  tagString: string;
  tags: string[];
  studio?: string | null;
  performers: string[];
  description: string;
  mediaInfoText: string;
  screenshots: Screenshot[];
  coverImageUrl?: string | null;
  hasLibraryCover: boolean;
  torrentPath?: string | null;
  infoHash?: string | null;
  torrentTotalBytes?: number | null;
  torrentPieceLength?: number | null;
  torrentPieceCount?: number | null;
  mediaPath?: string | null;
  lastError?: string | null;
}

interface CountedValue {
  name: string;
  count: number;
}

interface PackStats {
  sceneCount: number;
  totalDurationSeconds: number;
  totalSizeBytes: number;
  shortestSeconds: number;
  longestSeconds: number;
  averageSeconds: number;
  medianSeconds: number;
  qualities: CountedValue[];
  performers: CountedValue[];
}

interface PackIssue {
  severity: "error" | "warning";
  message: string;
}

interface PackPackage {
  batchId: string;
  packType: string;
  theme: string;
  postTitle: string;
  tags: string[];
  tagString: string;
  description: string;
  coverItemId?: string | null;
  coverImageUrl?: string | null;
  editedFields: string[];
  stats: PackStats;
  issues: PackIssue[];
  screensShown: number;
  packFolder?: string | null;
  torrentPath?: string | null;
  infoHash?: string | null;
  torrentTotalBytes?: number | null;
  torrentPieceLength?: number | null;
  torrentPieceCount?: number | null;
}

interface BatchDetail {
  id: string;
  name: string;
  updatedAtUtc: string;
  items: PrepPackage[];
  /** Present when the batch holds more than one video: one torrent and one post for all of them. */
  pack?: PackPackage | null;
}

interface JobState {
  jobId: string;
  itemId: string;
  kind: string;
  percent: number;
  message?: string | null;
  finished: boolean;
  error?: string | null;
}

interface SpriteTile {
  startSeconds: number;
  endSeconds: number;
  x: number;
  y: number;
  width: number;
  height: number;
}

interface SpriteInfo {
  available: boolean;
  tileWidth: number;
  tileHeight: number;
  tiles: SpriteTile[];
}

// ─────────────────────────── formatting ───────────────────────────

function formatBytes(bytes?: number | null): string {
  if (!bytes || bytes <= 0) return "—";
  const units = ["B", "KiB", "MiB", "GiB", "TiB"];
  let size = bytes;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit += 1;
  }
  return `${size.toFixed(unit === 0 ? 0 : 2)} ${units[unit]}`;
}

function formatDuration(seconds: number): string {
  if (!seconds || seconds <= 0) return "—";
  const total = Math.round(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  return h > 0 ? `${h}h ${String(m).padStart(2, "0")}m` : `${m}m ${String(s).padStart(2, "0")}s`;
}

function formatTimestamp(seconds: number): string {
  const total = Math.max(0, Math.floor(seconds));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  return h > 0
    ? `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}`
    : `${m}:${String(s).padStart(2, "0")}`;
}

/** Valid piece sizes (powers of two, 16 KiB up to EMP's 8 MiB maximum). 0 means automatic. */
const PIECE_SIZE_OPTIONS = [
  { value: "0", label: "Automatic (recommended)" },
  ...[16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192].map((kib) => ({
    value: String(kib),
    label: kib >= 1024 ? `${kib / 1024} MiB` : `${kib} KiB`,
  })),
];

const STATUS_LABELS: Record<string, string> = {
  pending: "Not started",
  screens: "Screens ready",
  ready: "Ready to upload",
  error: "Error",
};

// ─────────────────────────── small components ───────────────────────────

/**
 * A labelled field with explanatory text underneath.
 *
 * The host's shared `Field` takes a label and children only, so the hint renders as a sibling of
 * the input rather than as a prop — several of these settings need a sentence of explanation to be
 * usable, particularly the ones with disk-layout consequences.
 */
function Hinted({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <Field label={label}>
      {children}
      {hint ? <p className="empprep-hint empprep-field-hint">{hint}</p> : null}
    </Field>
  );
}

/** One row of the EMP upload form mirror: a right-aligned label beside its value, as on EMP. */
function EmpFormRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="empprep-emp-row">
      <div className="empprep-emp-label">{label}</div>
      <div className="empprep-emp-value">{children}</div>
    </div>
  );
}

/** EMP rejects an upload with fewer than this many tags. */
const EMP_MINIMUM_TAGS = 8;

/** Live tag count against EMP's minimum, counted the way EMP does: space-separated tokens. */
function TagCount({ tags }: { tags: string }) {
  const count = tags.split(/[\s,]+/).filter(Boolean).length;
  const short = count < EMP_MINIMUM_TAGS;
  return (
    <p className={`empprep-hint${short ? " empprep-warn" : ""}`}>
      {count} tag{count === 1 ? "" : "s"}
      {short ? ` — EMP needs at least ${EMP_MINIMUM_TAGS}. Add ${EMP_MINIMUM_TAGS - count} more.` : "."}
    </p>
  );
}

function CopyButton({ value, label = "Copy" }: { value: string; label?: string }) {
  const [copied, setCopied] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      if (timer.current) clearTimeout(timer.current);
      timer.current = setTimeout(() => setCopied(false), 1500);
    } catch {
      setCopied(false);
    }
  };

  return (
    <button type="button" className="empprep-copy" onClick={copy} disabled={!value}>
      {copied ? "Copied" : label}
    </button>
  );
}

function ProgressBar({ percent, label }: { percent: number; label?: string }) {
  return (
    <div className="empprep-progress">
      <div className="empprep-progress-track">
        <div className="empprep-progress-fill" style={{ width: `${Math.round(percent * 100)}%` }} />
      </div>
      <span className="empprep-progress-label">
        {label ? `${label} — ` : ""}
        {Math.round(percent * 100)}%
      </span>
    </div>
  );
}

const KIND_LABELS: Record<Screenshot["kind"], string> = {
  cover: "Cover",
  sheet: "Contact sheet",
  frame: "",
};

/** One image in the upload set, loaded through the authenticated artifact endpoint. */
function ScreenshotThumb({
  itemId,
  index,
  shot,
  onToggleFeatured,
}: {
  itemId: string;
  index: number;
  shot: Screenshot;
  onToggleFeatured?: () => void;
}) {
  const [src, setSrc] = useState<string | null>(null);

  useEffect(() => {
    let revoked: string | null = null;
    let cancelled = false;

    objectUrl(`/items/${itemId}/screenshots/${index}/file`)
      .then((url) => {
        if (cancelled) {
          URL.revokeObjectURL(url);
          return;
        }
        revoked = url;
        setSrc(url);
      })
      .catch(() => setSrc(null));

    return () => {
      cancelled = true;
      if (revoked) URL.revokeObjectURL(revoked);
    };
    // The path is part of the key: a regenerated set reuses indexes but not paths.
  }, [itemId, index, shot.path]);

  const label = KIND_LABELS[shot.kind] || formatTimestamp(shot.seconds);

  return (
    <figure className={`empprep-thumb${shot.featured ? " is-featured" : ""}`}>
      {src ? <img src={src} alt={label} /> : <div className="empprep-thumb-empty" />}
      <figcaption>
        <button
          type="button"
          className="empprep-star"
          onClick={onToggleFeatured}
          disabled={!onToggleFeatured}
          title={shot.featured ? "Full size — click to show as a thumbnail" : "Thumbnail — click to show full size"}
        >
          {shot.featured ? "★" : "☆"}
        </button>
        <span>{label}</span>
        {shot.url ? (
          <a className="empprep-thumb-hosted" href={shot.viewerUrl || shot.url} target="_blank" rel="noreferrer">
            hosted
          </a>
        ) : null}
      </figcaption>
    </figure>
  );
}

/**
 * Timestamp picker built on Cove's existing scrubbing sprite sheet.
 *
 * The sheet only supplies the timestamps: whatever is selected here is re-extracted from the source
 * file at full resolution, because a sprite tile is a 160px scrubbing thumbnail and would make a
 * poor screenshot.
 */
function SpritePicker({
  videoId,
  onCancel,
  onConfirm,
}: {
  videoId: number;
  onCancel: () => void;
  onConfirm: (timestamps: number[]) => void;
}) {
  const [info, setInfo] = useState<SpriteInfo | null>(null);
  const [sheetUrl, setSheetUrl] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let revoked: string | null = null;
    let cancelled = false;

    get(`/videos/${videoId}/sprite`)
      .then(async (result: SpriteInfo) => {
        if (cancelled) return;
        setInfo(result);
        if (!result.available) return;
        const url = await objectUrl(`/videos/${videoId}/sprite/image`);
        if (cancelled) {
          URL.revokeObjectURL(url);
          return;
        }
        revoked = url;
        setSheetUrl(url);
      })
      .catch((err: Error) => !cancelled && setError(err.message));

    return () => {
      cancelled = true;
      if (revoked) URL.revokeObjectURL(revoked);
    };
  }, [videoId]);

  const toggle = (index: number) => {
    setSelected((previous) => {
      const next = new Set(previous);
      if (next.has(index)) next.delete(index);
      else next.add(index);
      return next;
    });
  };

  const timestamps = useMemo(
    () => (info ? [...selected].sort((a, b) => a - b).map((index) => info.tiles[index].startSeconds) : []),
    [selected, info],
  );

  return (
    <div className="empprep-modal-backdrop" onClick={onCancel}>
      <div className="empprep-modal" onClick={(event) => event.stopPropagation()}>
        <header>
          <h3>Pick screenshot timestamps</h3>
          <p className="empprep-hint">
            Frames are re-extracted from the source file at full resolution — these tiles are only for
            choosing the moments.
          </p>
        </header>

        {error ? <p className="empprep-error">{error}</p> : null}

        {info && !info.available ? (
          <p className="empprep-hint">
            Cove has not generated a sprite sheet for this video, so there is nothing to pick from. Run
            Cove&apos;s sprite generation for it, or just generate screenshots and let the automatic
            spacing choose the moments.
          </p>
        ) : null}

        {info?.available && sheetUrl ? (
          <div className="empprep-sprite-grid">
            {info.tiles.map((tile, index) => (
              <button
                type="button"
                key={`${tile.x}-${tile.y}-${tile.startSeconds}`}
                className={`empprep-sprite-tile${selected.has(index) ? " is-selected" : ""}`}
                onClick={() => toggle(index)}
                title={formatTimestamp(tile.startSeconds)}
                style={{
                  width: tile.width,
                  height: tile.height,
                  backgroundImage: `url(${sheetUrl})`,
                  backgroundPosition: `-${tile.x}px -${tile.y}px`,
                }}
              >
                <span>{formatTimestamp(tile.startSeconds)}</span>
              </button>
            ))}
          </div>
        ) : null}

        <footer>
          <span className="empprep-hint">{selected.size} selected</span>
          <div className="empprep-actions">
            <button type="button" className="empprep-btn" onClick={onCancel}>
              Cancel
            </button>
            <button
              type="button"
              className="empprep-btn empprep-btn-primary"
              disabled={selected.size === 0}
              onClick={() => onConfirm(timestamps)}
            >
              Generate {selected.size} frame{selected.size === 1 ? "" : "s"}
            </button>
          </div>
        </footer>
      </div>
    </div>
  );
}

// ─────────────────────────── item card ───────────────────────────

function ItemCard({
  item,
  job,
  onChanged,
  onRemoved,
  onError,
  onJobStarted,
  inPack = false,
}: {
  item: PrepPackage;
  job?: JobState;
  onChanged: (updated: PrepPackage) => void;
  onRemoved: () => void;
  onError: (message: string) => void;
  onJobStarted: (jobId: string | undefined) => void;
  /** In a pack, the pack owns the post, uploads and torrent; the card only prepares screens. */
  inPack?: boolean;
}) {
  const [expanded, setExpanded] = useState(false);
  const [title, setTitle] = useState(item.title);
  const [postTitle, setPostTitle] = useState(item.postTitle);
  const [releaseName, setReleaseName] = useState(item.releaseName);
  const [tags, setTags] = useState(item.tagString);
  const [description, setDescription] = useState(item.description);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);

  // Fields changed since the last save. Only these — and fields already holding an edit — are
  // submitted; the rest go as null ("use the generated value"). Submitting everything would freeze
  // values the user never touched, e.g. a post title rendered from a scene title they then changed.
  const touched = useRef<Set<string>>(new Set());
  const change = (field: string, setter: (value: string) => void) => (value: string) => {
    touched.current.add(field);
    setter(value);
    setDirty(true);
  };
  const submitted = (field: string, value: string) =>
    touched.current.has(field) || item.editedFields?.includes(field) ? value : null;
  const [picking, setPicking] = useState(false);

  // Re-sync when the server view changes underneath us — a finished job rewrites the description
  // with the new screenshot grid, which would otherwise be clobbered by the stale local copy.
  useEffect(() => {
    if (dirty) return;
    setTitle(item.title);
    setPostTitle(item.postTitle);
    setReleaseName(item.releaseName);
    setTags(item.tagString);
    setDescription(item.description);
  }, [item.title, item.postTitle, item.releaseName, item.tagString, item.description, dirty]);

  const busy = job !== undefined && !job.finished;

  const run = async (path: string, body?: any) => {
    try {
      const started = await post(path, body);
      onJobStarted(started?.jobId);
    } catch (error) {
      onError((error as Error).message);
    }
  };

  const setFeatured = async (index: number, featured: boolean) => {
    try {
      onChanged(await put(`/items/${item.itemId}/screenshots/${index}/featured`, { featured }));
    } catch (error) {
      onError((error as Error).message);
    }
  };

  const save = async () => {
    setSaving(true);
    try {
      const updated = await put(`/items/${item.itemId}`, {
        title: submitted("title", title),
        postTitle: submitted("postTitle", postTitle),
        tags: submitted("tags", tags),
        description: submitted("description", description),
        releaseName: touched.current.has("releaseName") ? releaseName : null,
      });
      touched.current.clear();
      setDirty(false);
      onChanged(updated);
    } catch (error) {
      onError((error as Error).message);
    } finally {
      setSaving(false);
    }
  };

  const resetOverrides = async () => {
    setSaving(true);
    try {
      const updated = await put(`/items/${item.itemId}`, {
        title: null,
        postTitle: null,
        tags: null,
        description: null,
        releaseName: null,
      });
      touched.current.clear();
      setDirty(false);
      onChanged(updated);
    } catch (error) {
      onError((error as Error).message);
    } finally {
      setSaving(false);
    }
  };

  const downloadTorrent = async () => {
    try {
      const url = await objectUrl(`/items/${item.itemId}/torrent/file`);
      const link = document.createElement("a");
      link.href = url;
      link.download = `${item.releaseName || "release"}.torrent`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      // Revoke once the browser has had a chance to start the download.
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    } catch (error) {
      onError((error as Error).message);
    }
  };

  const uploadSet = item.screenshots.filter((shot) => shot.uploads);
  const hostedCount = uploadSet.filter((shot) => !!shot.url).length;
  const fullSizeCount = item.screenshots.filter((shot) => shot.isFullSize).length;
  const hasSheet = item.screenshots.some((shot) => shot.kind === "sheet");

  return (
    <article className={`empprep-item empprep-status-${item.status}`}>
      <header className="empprep-item-head" onClick={() => setExpanded((value) => !value)}>
        <button type="button" className="empprep-disclose" aria-label={expanded ? "Collapse" : "Expand"}>
          {expanded ? "▾" : "▸"}
        </button>
        <div className="empprep-item-title">
          <strong>{item.releaseName || item.title || `Video ${item.videoId}`}</strong>
          <span className="empprep-item-sub">
            {item.studio || "No studio"}
            {item.performers.length > 0 ? ` · ${item.performers.join(", ")}` : ""}
          </span>
        </div>
        <span className={`empprep-badge empprep-badge-${item.status}`}>
          {STATUS_LABELS[item.status] ?? item.status}
        </span>
        <span className="empprep-item-meta">
          {inPack
            ? hasSheet ? "screens ready" : "no screens yet"
            : item.screenshots.length > 0
              ? `${fullSizeCount} full-size${hasSheet ? " + contact sheet" : ""} · ${hostedCount}/${uploadSet.length} on hamsterimg`
              : "no screens"}
        </span>
      </header>

      {busy ? <ProgressBar percent={job!.percent} label={job!.kind} /> : null}

      {item.lastError ? <p className="empprep-error">{item.lastError}</p> : null}
      {job?.error ? <p className="empprep-error">{job.error}</p> : null}

      {expanded ? (
        <div className="empprep-item-body">
          <div className="empprep-toolbar">
            <button type="button" className="empprep-btn empprep-btn-primary" disabled={busy}
              onClick={() => run(`/items/${item.itemId}/screenshots`)}>
              Generate screens
            </button>
            <button type="button" className="empprep-btn" disabled={busy} onClick={() => setPicking(true)}>
              Pick timestamps…
            </button>
            {inPack ? null : (
              <>
                <button type="button" className="empprep-btn" disabled={busy || item.screenshots.length === 0}
                  onClick={() => run(`/items/${item.itemId}/screenshots/upload`)}>
                  Upload to hamsterimg
                </button>
                <button type="button" className="empprep-btn empprep-btn-primary" disabled={busy}
                  onClick={() => run(`/items/${item.itemId}/torrent`)}>
                  Build torrent
                </button>
              </>
            )}
            <span className="empprep-spacer" />
            <button type="button" className="empprep-btn empprep-btn-danger" disabled={busy} onClick={onRemoved}>
              Remove
            </button>
          </div>

          {inPack ? null : (
          <>
          <section className="empprep-emp-form">
            <h4>EMP upload form</h4>
            <p className="empprep-hint">
              Everything EMP&apos;s upload page asks for, in the same order. Pick the category on EMP itself.
            </p>

            <EmpFormRow label="Torrent file">
              {item.torrentPath ? (
                <div className="empprep-row empprep-row-inline">
                  <button type="button" className="empprep-btn" onClick={downloadTorrent}>
                    Download .torrent
                  </button>
                  <span className="empprep-hint">{item.torrentPath.split(/[\\/]/).pop()}</span>
                </div>
              ) : (
                <span className="empprep-hint">Press Build torrent first.</span>
              )}
            </EmpFormRow>

            <EmpFormRow label="Category">
              <span className="empprep-hint">Choose on EMP.</span>
            </EmpFormRow>

            <EmpFormRow label="Title">
              <div className="empprep-row">
                <TextInput value={postTitle} onChange={change("postTitle", setPostTitle)} />
                <CopyButton value={postTitle} />
              </div>
            </EmpFormRow>

            <EmpFormRow label="Tags">
              <div className="empprep-row">
                <TextArea rows={3} value={tags} onChange={change("tags", setTags)} />
                <CopyButton value={tags} />
              </div>
              <TagCount tags={tags} />
            </EmpFormRow>

            <EmpFormRow label="Cover image">
              <div className="empprep-row">
                <span className="empprep-readonly">
                  {item.coverImageUrl || (item.screenshots.length > 0 ? "Upload to hamsterimg first" : "Generate screens first")}
                </span>
                <CopyButton value={item.coverImageUrl || ""} />
              </div>
            </EmpFormRow>

            <EmpFormRow label="Description">
              <div className="empprep-row">
                <TextArea rows={16} value={description}
                  onChange={change("description", setDescription)} />
                <CopyButton value={description} />
              </div>
            </EmpFormRow>
          </section>

          <section>
            <h4>Naming and scene details</h4>
            <div className="empprep-grid">
              <Hinted label="Release name" hint="The torrent's file or folder name.">
                <div className="empprep-row">
                  <TextInput value={releaseName} onChange={change("releaseName", setReleaseName)} />
                  <CopyButton value={releaseName} />
                </div>
              </Hinted>

              <Hinted label="Scene title" hint="Feeds the EMP title and the description.">
                <div className="empprep-row">
                  <TextInput value={title} onChange={change("title", setTitle)} />
                  <CopyButton value={title} />
                </div>
              </Hinted>

              <Hinted label="Studio">
                <div className="empprep-row">
                  <span className="empprep-readonly">{item.studio || "—"}</span>
                  <CopyButton value={item.studio || ""} />
                </div>
              </Hinted>

              <Hinted label="Performers">
                <div className="empprep-row">
                  <span className="empprep-readonly">{item.performers.join(", ") || "—"}</span>
                  <CopyButton value={item.performers.join(", ")} />
                </div>
              </Hinted>
            </div>
          </section>

          <div className="empprep-actions empprep-save-row">
            <SaveButton loading={saving} onClick={save} />
            <button type="button" className="empprep-btn" disabled={saving} onClick={resetOverrides}>
              Reset to library values
            </button>
            {dirty ? <span className="empprep-hint">Unsaved edits</span> : null}
          </div>
          </>
          )}

          {inPack && hasSheet ? (
            <section>
              <h4>Contact sheet</h4>
              <p className="empprep-hint">Goes into the pack&apos;s Screens folder, and into the post as a thumbnail.</p>
              <div className="empprep-thumbs">
                {item.screenshots.map((shot, index) =>
                  shot.kind === "sheet" ? (
                    <ScreenshotThumb key={shot.path} itemId={item.itemId} index={index} shot={shot} />
                  ) : null,
                )}
              </div>
            </section>
          ) : null}

          {item.screenshots.length > 0 && !inPack ? (
            <section>
              <h4>Uploaded images</h4>
              <p className="empprep-hint">
                Only these go to hamsterimg: the full-size images (the first is the cover image) and the
                contact sheet, which is the screens section.
                {hostedCount < uploadSet.length
                  ? ` ${uploadSet.length - hostedCount} not uploaded yet — the description shows a [NOT UPLOADED] marker for those.`
                  : ""}
              </p>
              <div className="empprep-thumbs">
                {item.screenshots.map((shot, index) =>
                  shot.uploads ? (
                    <ScreenshotThumb
                      key={shot.path}
                      itemId={item.itemId}
                      index={index}
                      shot={shot}
                      onToggleFeatured={
                        busy || shot.kind === "sheet" ? undefined : () => setFeatured(index, !shot.featured)
                      }
                    />
                  ) : null,
                )}
              </div>

              <details className="empprep-local-frames">
                <summary>
                  Frames in the contact sheet ({item.screenshots.filter((shot) => !shot.uploads).length}) — kept
                  local; star one to upload it full size instead
                </summary>
                <div className="empprep-thumbs">
                  {item.screenshots.map((shot, index) =>
                    shot.uploads ? null : (
                      <ScreenshotThumb
                        key={shot.path}
                        itemId={item.itemId}
                        index={index}
                        shot={shot}
                        onToggleFeatured={busy ? undefined : () => setFeatured(index, true)}
                      />
                    ),
                  )}
                </div>
              </details>
            </section>
          ) : null}

          {inPack ? null : (
          <section>
            <h4>Torrent</h4>
            {item.torrentPath ? (
              <dl className="empprep-facts">
                <dt>Infohash</dt>
                <dd>
                  <code>{item.infoHash}</code> <CopyButton value={item.infoHash || ""} />
                </dd>
                <dt>Payload size</dt>
                <dd>{formatBytes(item.torrentTotalBytes)}</dd>
                <dt>Piece size</dt>
                <dd>
                  {item.torrentPieceLength
                    ? `${formatBytes(item.torrentPieceLength)} × ${(item.torrentPieceCount ?? 0).toLocaleString()} pieces`
                    : "— (rebuild to record)"}
                </dd>
                <dt>Torrent file</dt>
                <dd>
                  <button type="button" className="empprep-btn" onClick={downloadTorrent}>
                    Download .torrent
                  </button>
                  <CopyButton value={item.torrentPath} label="Copy path" />
                </dd>
                <dt>Seed from</dt>
                <dd>
                  <code>{item.mediaPath ? item.mediaPath.replace(/\/[^/]*$/, "") : "—"}</code>
                  <CopyButton value={item.mediaPath ? item.mediaPath.replace(/\/[^/]*$/, "") : ""} label="Copy" />
                </dd>
              </dl>
            ) : (
              <p className="empprep-hint">
                No torrent yet. Building one hashes the file where it already is — point your client at the
                folder above as the save path and it will seed without a second copy.
              </p>
            )}
          </section>

          )}

          <section>
            <h4>Media info</h4>
            <div className="empprep-row">
              <pre className="empprep-pre">{item.mediaInfoText}</pre>
              <CopyButton value={item.mediaInfoText} />
            </div>
          </section>
        </div>
      ) : null}

      {picking ? (
        <SpritePicker
          videoId={item.videoId}
          onCancel={() => setPicking(false)}
          onConfirm={(timestamps) => {
            setPicking(false);
            void run(`/items/${item.itemId}/screenshots`, { timestamps });
          }}
        />
      ) : null}
    </article>
  );
}

// ─────────────────────────── pack ───────────────────────────

/**
 * The post and torrent for a multi-video batch: one EMP upload for every scene in it.
 *
 * The scenes' videos are hard-linked into a folder under the pack folder setting — second names for
 * the same files, so nothing is copied and nothing in the library moves — together with a Screens
 * folder holding each scene's contact sheet, as EMP's pack rules require.
 */
function PackPanel({
  batchId,
  pack,
  items,
  job,
  onChanged,
  onError,
  onJobStarted,
}: {
  batchId: string;
  pack: PackPackage;
  items: PrepPackage[];
  job?: JobState;
  onChanged: (pack: PackPackage) => void;
  onError: (message: string) => void;
  onJobStarted: (jobId: string | undefined) => void;
}) {
  const [theme, setTheme] = useState(pack.theme);
  const [postTitle, setPostTitle] = useState(pack.postTitle);
  const [tags, setTags] = useState(pack.tagString);
  const [description, setDescription] = useState(pack.description);
  const [coverItemId, setCoverItemId] = useState(pack.coverItemId ?? "");
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);

  // As on a single video: only fields the user changed — or that already hold an edit — are sent,
  // so untouched fields keep following the scenes and settings.
  const touched = useRef<Set<string>>(new Set());
  const change = (field: string, setter: (value: string) => void) => (value: string) => {
    touched.current.add(field);
    setter(value);
    setDirty(true);
  };
  const submitted = (field: string, value: string) =>
    touched.current.has(field) || pack.editedFields?.includes(field) ? value : null;

  useEffect(() => {
    if (dirty) return;
    setTheme(pack.theme);
    setPostTitle(pack.postTitle);
    setTags(pack.tagString);
    setDescription(pack.description);
    setCoverItemId(pack.coverItemId ?? "");
  }, [pack.theme, pack.postTitle, pack.tagString, pack.description, pack.coverItemId, dirty]);

  const busy = job !== undefined && !job.finished;
  const errors = pack.issues.filter((issue) => issue.severity === "error");
  const warnings = pack.issues.filter((issue) => issue.severity === "warning");
  const missingScreens = items.filter((item) => !item.screenshots.some((shot) => shot.kind === "sheet"));

  const save = async (body: Record<string, unknown>) => {
    setSaving(true);
    try {
      onChanged(await put(`/batches/${batchId}/pack`, body));
      touched.current.clear();
      setDirty(false);
    } catch (error) {
      onError((error as Error).message);
    } finally {
      setSaving(false);
    }
  };

  const saveEdits = () =>
    save({
      theme,
      postTitle: submitted("postTitle", postTitle),
      tags: submitted("tags", tags),
      description: submitted("description", description),
      coverItemId: coverItemId || null,
    });

  const resetEdits = () => save({ theme, postTitle: null, tags: null, description: null, coverItemId: coverItemId || null });

  const run = async (path: string, body?: unknown) => {
    try {
      const started = await post(path, body);
      onJobStarted(started?.jobId);
    } catch (error) {
      onError((error as Error).message);
    }
  };

  // One job for the whole pack: it works through the scenes in turn, instead of a job per scene
  // all starting at once.
  const screensRunSize = useRef(0);
  const generateScreens = (missingOnly: boolean) => {
    if (!missingOnly && !window.confirm(`Regenerate screens for all ${items.length} scenes? Existing screens are replaced.`)) return;
    screensRunSize.current = missingOnly ? missingScreens.length : items.length;
    void run(`/batches/${batchId}/screens`, { missingOnly });
  };

  const downloadTorrent = async () => {
    try {
      const url = await objectUrl(`/batches/${batchId}/pack/torrent/file`);
      const link = document.createElement("a");
      link.href = url;
      link.download = `${pack.postTitle || "pack"}.torrent`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    } catch (error) {
      onError((error as Error).message);
    }
  };

  const seedFrom = pack.packFolder ? pack.packFolder.replace(/[\\/][^\\/]*$/, "") : null;
  const stats = pack.stats;
  const quality = stats.qualities
    .map((entry) => `${entry.name} ${Math.round((entry.count / Math.max(1, stats.sceneCount)) * 100)}%`)
    .join(" · ");

  return (
    <section className="empprep-pack">
      <header className="empprep-pack-head">
        <div>
          <h3>
            {pack.packType} · {stats.sceneCount} scenes
          </h3>
          <p className="empprep-hint">
            {formatDuration(stats.totalDurationSeconds)} · {formatBytes(stats.totalSizeBytes)} · {quality || "quality unknown"} ·
            {" "}shortest {formatDuration(stats.shortestSeconds)}, longest {formatDuration(stats.longestSeconds)}, average{" "}
            {formatDuration(stats.averageSeconds)}
          </p>
        </div>
      </header>

      {errors.length + warnings.length > 0 ? (
        <ul className="empprep-issues">
          {errors.map((issue) => (
            <li key={issue.message} className="empprep-issue-error">{issue.message}</li>
          ))}
          {warnings.map((issue) => (
            <li key={issue.message} className="empprep-issue-warning">{issue.message}</li>
          ))}
        </ul>
      ) : null}

      <div className="empprep-toolbar">
        <button type="button" className="empprep-btn empprep-btn-primary" disabled={busy || missingScreens.length === 0}
          onClick={() => generateScreens(true)}>
          {missingScreens.length > 0 ? `Generate screens for ${missingScreens.length} scene${missingScreens.length === 1 ? "" : "s"}` : "All scenes have screens"}
        </button>
        <button type="button" className="empprep-btn" disabled={busy} onClick={() => generateScreens(false)}>
          Regenerate all screens
        </button>
        <button type="button" className="empprep-btn" disabled={busy} onClick={() => run(`/batches/${batchId}/pack/upload`)}>
          Upload to hamsterimg
        </button>
        <button type="button" className="empprep-btn empprep-btn-primary" disabled={busy || errors.length > 0}
          onClick={() => run(`/batches/${batchId}/pack/torrent`)}>
          Build pack torrent
        </button>
      </div>

      {busy ? (
        <ProgressBar
          percent={job!.percent}
          label={job!.kind === "screenshots"
            ? `screens, scene ${Math.min(screensRunSize.current || items.length, Math.floor(job!.percent * (screensRunSize.current || items.length)) + 1)} of ${screensRunSize.current || items.length}`
            : job!.kind === "torrent" ? "hashing the pack" : job!.kind}
        />
      ) : null}
      {job?.error ? <p className="empprep-error">{job.error}</p> : null}

      <Hinted
        label="What this pack has in common"
        hint="Required by EMP. Say what ties the scenes together — the performer, the source sites, the fetish — and what happens in them. It leads the description."
      >
        <TextArea rows={3} value={theme} onChange={change("theme", setTheme)}
          placeholder="e.g. Every Spyder445 scene sourced from Pornhub, OnlyFans and ManyVids — mostly oiled solo play with toys." />
      </Hinted>

      <section className="empprep-emp-form">
        <h4>EMP upload form</h4>
        <p className="empprep-hint">One post for the whole pack, in the same order as EMP&apos;s upload page.</p>

        <EmpFormRow label="Torrent file">
          {pack.torrentPath ? (
            <div className="empprep-row empprep-row-inline">
              <button type="button" className="empprep-btn" onClick={downloadTorrent}>Download .torrent</button>
              <span className="empprep-hint">
                {pack.torrentPieceLength
                  ? `${formatBytes(pack.torrentTotalBytes)} · ${formatBytes(pack.torrentPieceLength)} × ${(pack.torrentPieceCount ?? 0).toLocaleString()} pieces`
                  : null}
              </span>
            </div>
          ) : (
            <span className="empprep-hint">Press Build pack torrent first.</span>
          )}
        </EmpFormRow>

        <EmpFormRow label="Category">
          <span className="empprep-hint">Choose on EMP.</span>
        </EmpFormRow>

        <EmpFormRow label="Title">
          <div className="empprep-row">
            <TextInput value={postTitle} onChange={change("postTitle", setPostTitle)} />
            <CopyButton value={postTitle} />
          </div>
        </EmpFormRow>

        <EmpFormRow label="Tags">
          <div className="empprep-row">
            <TextArea rows={3} value={tags} onChange={change("tags", setTags)} />
            <CopyButton value={tags} />
          </div>
          <TagCount tags={tags} />
        </EmpFormRow>

        <EmpFormRow label="Cover image">
          <div className="empprep-row">
            <SelectInput
              value={coverItemId}
              onChange={(value: string) => { setCoverItemId(value); setDirty(true); }}
              options={items
                .filter((item) => item.screenshots.some((shot) => shot.kind === "cover"))
                .map((item) => ({ value: item.itemId, label: `Cover of ${item.title || `video ${item.videoId}`}` }))}
            />
          </div>
          <div className="empprep-row">
            <span className="empprep-readonly">
              {pack.coverImageUrl || "Upload to hamsterimg first"}
            </span>
            <CopyButton value={pack.coverImageUrl || ""} />
          </div>
        </EmpFormRow>

        <EmpFormRow label="Description">
          <div className="empprep-row">
            <TextArea rows={18} value={description} onChange={change("description", setDescription)} />
            <CopyButton value={description} />
          </div>
          <p className="empprep-hint">
            Screens for {pack.screensShown} of {stats.sceneCount} scenes appear in the post
            {pack.screensShown < stats.sceneCount ? " (EMP allows a representative share above 50 scenes)" : ""}; every scene&apos;s
            screens are in the torrent&apos;s Screens folder.
          </p>
        </EmpFormRow>
      </section>

      <div className="empprep-actions empprep-save-row">
        <SaveButton loading={saving} onClick={saveEdits} />
        <button type="button" className="empprep-btn" disabled={saving} onClick={resetEdits}>
          Reset to generated values
        </button>
        {dirty ? <span className="empprep-hint">Unsaved edits</span> : null}
      </div>

      {pack.packFolder ? (
        <section>
          <h4>Seeding</h4>
          <dl className="empprep-facts">
            <dt>Pack folder</dt>
            <dd><code>{pack.packFolder}</code> <CopyButton value={pack.packFolder} /></dd>
            <dt>Seed from</dt>
            <dd><code>{seedFrom}</code> <CopyButton value={seedFrom ?? ""} /></dd>
            <dt>Infohash</dt>
            <dd><code>{pack.infoHash}</code> <CopyButton value={pack.infoHash ?? ""} /></dd>
          </dl>
          <p className="empprep-hint">
            Add the .torrent to your client with the &quot;Seed from&quot; folder as the save path. The videos in the pack folder
            are hard links to your library files — the same data under a second name — so they take no extra space, and
            deleting the pack folder later never touches the library.
          </p>
        </section>
      ) : null}
    </section>
  );
}

// ─────────────────────────── page ───────────────────────────

export function EmpPrepPage() {
  const [batches, setBatches] = useState<BatchSummary[]>([]);
  const [activeBatchId, setActiveBatchId] = useState<string | null>(null);
  const [detail, setDetail] = useState<BatchDetail | null>(null);
  const [jobs, setJobs] = useState<JobState[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [newBatchName, setNewBatchName] = useState("");

  const anyJobRunning = jobs.some((job) => !job.finished);

  const loadBatches = useCallback(async () => {
    try {
      const result: BatchSummary[] = await get("/batches");
      setBatches(result);
      setActiveBatchId((current) => current ?? result[0]?.id ?? null);
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setLoading(false);
    }
  }, []);

  const loadDetail = useCallback(async (batchId: string) => {
    try {
      setDetail(await get(`/batches/${batchId}`));
    } catch (err) {
      setError((err as Error).message);
    }
  }, []);

  useEffect(() => { void loadBatches(); }, [loadBatches]);

  useEffect(() => {
    if (activeBatchId) void loadDetail(activeBatchId);
    else setDetail(null);
  }, [activeBatchId, loadDetail]);

  // Jobs this page started and has not yet seen finish. Tracked by id rather than inferred from a
  // running→finished transition: a short job (extracting a dozen frames takes well under a second)
  // can start and finish between two polls, and a page waiting to *see* it running would never
  // refresh the card — which is exactly how finished screenshots went missing from the page.
  const pendingJobIds = useRef<Set<string>>(new Set());
  const [pendingCount, setPendingCount] = useState(0);

  const pollJobs = useCallback(async () => {
    try {
      const active: JobState[] = await get("/jobs");
      setJobs(active);

      let settled = false;
      for (const jobId of [...pendingJobIds.current]) {
        const state = active.find((job) => job.jobId === jobId);
        // Finished, or already pruned from the tracker: either way the result is on the server.
        if (!state || state.finished) {
          pendingJobIds.current.delete(jobId);
          settled = true;
        }
      }

      if (settled) {
        setPendingCount(pendingJobIds.current.size);
        if (activeBatchId) await loadDetail(activeBatchId);
        await loadBatches();
      }
    } catch {
      // A transient poll failure is not worth surfacing; the next tick retries.
    }
  }, [activeBatchId, loadDetail, loadBatches]);

  const trackJob = useCallback(
    (jobId: string | undefined) => {
      if (!jobId) return;
      pendingJobIds.current.add(jobId);
      setPendingCount(pendingJobIds.current.size);
      // Poll straight away rather than waiting out the interval.
      void pollJobs();
    },
    [pollJobs],
  );

  // Poll quickly while this page is waiting on work, slowly otherwise.
  useEffect(() => {
    const busy = anyJobRunning || pendingCount > 0;
    const interval = setInterval(() => void pollJobs(), busy ? 750 : 5000);
    return () => clearInterval(interval);
  }, [anyJobRunning, pendingCount, pollJobs]);

  const createBatch = async () => {
    try {
      const batch = await post("/batches", { name: newBatchName });
      setNewBatchName("");
      await loadBatches();
      setActiveBatchId(batch.id);
    } catch (err) {
      setError((err as Error).message);
    }
  };

  const deleteBatch = async (batchId: string) => {
    if (!window.confirm("Delete this prep batch? Generated screenshots and torrents stay on disk.")) return;
    try {
      await del(`/batches/${batchId}`);
      setActiveBatchId(null);
      await loadBatches();
    } catch (err) {
      setError((err as Error).message);
    }
  };

  const removeItem = async (itemId: string) => {
    try {
      await del(`/items/${itemId}`);
      if (activeBatchId) await loadDetail(activeBatchId);
      await loadBatches();
    } catch (err) {
      setError((err as Error).message);
    }
  };

  const replaceItem = (updated: PrepPackage) => {
    setDetail((current) =>
      current
        ? { ...current, items: current.items.map((item) => (item.itemId === updated.itemId ? updated : item)) }
        : current,
    );
  };

  const jobFor = (itemId: string) => jobs.find((job) => job.itemId === itemId);

  return (
    <div className="empprep-shell">
      <header className="empprep-header">
        <div>
          <h1>EMP Prep</h1>
          <p className="empprep-hint">
            Screens, tags, description and a private torrent — built from your files where they already sit.
          </p>
        </div>
      </header>

      {error ? (
        <p className="empprep-error">
          {error} <button type="button" className="empprep-copy" onClick={() => setError(null)}>dismiss</button>
        </p>
      ) : null}

      <div className="empprep-layout">
        <aside className="empprep-sidebar">
          <h2>Batches</h2>
          {loading ? <p className="empprep-hint">Loading…</p> : null}
          {!loading && batches.length === 0 ? (
            <p className="empprep-hint">
              No batches yet. Select videos in the library and use <strong>Add to EMP prep</strong>, or create
              an empty batch below.
            </p>
          ) : null}
          <ul className="empprep-batch-list">
            {batches.map((batch) => (
              <li key={batch.id}>
                <button
                  type="button"
                  className={`empprep-batch${batch.id === activeBatchId ? " is-active" : ""}`}
                  onClick={() => setActiveBatchId(batch.id)}
                >
                  <span className="empprep-batch-name">{batch.name}</span>
                  <span className="empprep-batch-count">
                    {batch.readyCount}/{batch.itemCount}
                  </span>
                </button>
              </li>
            ))}
          </ul>

          <div className="empprep-new-batch">
            <TextInput value={newBatchName} onChange={setNewBatchName} placeholder="New batch name" />
            <button type="button" className="empprep-btn" onClick={createBatch}>
              Create
            </button>
          </div>
        </aside>

        <main className="empprep-main">
          {detail ? (
            <>
              <div className="empprep-batch-head">
                <h2>{detail.name}</h2>
                <button type="button" className="empprep-btn empprep-btn-danger" onClick={() => deleteBatch(detail.id)}>
                  Delete batch
                </button>
              </div>

              {detail.items.length === 0 ? (
                <p className="empprep-hint">
                  This batch is empty. Select videos in the library and use <strong>Add to EMP prep</strong>.
                </p>
              ) : null}

              {detail.pack ? (
                <PackPanel
                  batchId={detail.id}
                  pack={detail.pack}
                  items={detail.items}
                  job={jobFor(detail.id)}
                  onChanged={(pack) => setDetail((current) => (current ? { ...current, pack } : current))}
                  onError={setError}
                  onJobStarted={trackJob}
                />
              ) : null}

              {detail.pack ? <h3 className="empprep-scenes-head">Scenes</h3> : null}

              {detail.items.map((item) => (
                <ItemCard
                  key={item.itemId}
                  item={item}
                  job={jobFor(item.itemId)}
                  onChanged={replaceItem}
                  onRemoved={() => removeItem(item.itemId)}
                  onError={setError}
                  onJobStarted={trackJob}
                  inPack={!!detail.pack}
                />
              ))}
            </>
          ) : (
            <p className="empprep-hint">Select or create a batch to get started.</p>
          )}
        </main>
      </div>
    </div>
  );
}

// ─────────────────────────── settings ───────────────────────────

export function EmpPrepSettingsPanel() {
  const [settings, setSettings] = useState<any>(null);
  const [meta, setMeta] = useState<any>(null);
  // Saving is assumed and never announced; only a failed save is shown, since it means the edit
  // was not kept.
  const [saveError, setSaveError] = useState<string | null>(null);

  // Cove's settings convention: no Save button. Text and number fields are written when focus leaves
  // them; checkboxes and dropdowns are written as soon as they change.
  //
  // `latest` always holds the newest values so a commit never sends a stale closure, and `dirty`
  // records whether anything changed since the last write, so tabbing through fields without typing
  // does not send a request. Writes are chained so an older request can never land after a newer one.
  const latest = useRef<any>(null);
  const dirty = useRef(false);
  const revision = useRef(0);
  const queue = useRef<Promise<void>>(Promise.resolve());

  useEffect(() => {
    get("/settings")
      .then((result) => {
        latest.current = result.settings;
        setSettings(result.settings);
        setMeta(result);
      })
      .catch((error: Error) => setSaveError(error.message));
  }, []);

  const persist = useCallback(() => {
    if (!dirty.current || !latest.current) return queue.current;
    dirty.current = false;
    const snapshot = latest.current;
    const sentRevision = revision.current;

    queue.current = queue.current.then(async () => {
      try {
        const saved = await put("/settings", snapshot);
        // The server normalises values (clamping, rounding piece sizes). Show that result, unless the
        // user has already changed something else while this request was in flight.
        if (revision.current === sentRevision) {
          latest.current = saved;
          setSettings(saved);
        }
        setMeta(await get("/settings"));
        setSaveError(null);
      } catch (error) {
        // Keep the edit so the next commit retries it.
        dirty.current = true;
        setSaveError(`Not saved: ${(error as Error).message}`);
      }
    });
    return queue.current;
  }, []);

  /** Record a change without writing it yet; it is written when the field loses focus. */
  const update = (key: string, value: any) => {
    latest.current = { ...latest.current, [key]: value };
    dirty.current = true;
    revision.current += 1;
    setSettings(latest.current);
  };

  /** Record and write a change immediately — for checkboxes and dropdowns. */
  const updateNow = (key: string, value: any) => {
    update(key, value);
    void persist();
  };

  // Leaving the settings page mid-edit (focus never leaves the field) must not lose the edit.
  useEffect(() => () => void persist(), [persist]);

  if (!settings) {
    return <div className="empprep-settings-panel"><p className="empprep-hint">{saveError ?? "Loading…"}</p></div>;
  }

  return (
    <div className="empprep-settings-panel" onBlur={() => void persist()}>
      {saveError ? (
        <p className="empprep-error" role="alert">
          {saveError}
        </p>
      ) : null}

      <section>
        <h3>Tracker</h3>
        <Hinted
          label="Announce URL"
          hint="Your full announce URL including passkey. Required before a torrent can be built. Every torrent EMP Prep creates is flagged private."
        >
          <TextInput value={settings.announceUrl} onChange={(value: string) => update("announceUrl", value)}
            placeholder="https://tracker.example/passkey/announce" />
        </Hinted>
        {!meta?.announceConfigured ? (
          <p className="empprep-hint empprep-warn">No announce URL set — torrent building will refuse until this is filled in.</p>
        ) : null}

        <Hinted label="Source tag" hint="Written to the torrent's info.source field, which makes the infohash unique to this tracker.">
          <TextInput value={settings.source} onChange={(value: string) => update("source", value)} />
        </Hinted>

        <Hinted
          label="Piece size"
          hint="Automatic picks the standard size for the file: about 512 KiB for 700 MB, 1 MiB at 1 GB, 2 MiB at 2 GB, 4 MiB at 4 GB, 8 MiB from 8 GB. EMP rejects pieces above 8 MiB, so that is the ceiling — large packs just have more pieces. Leave it on Automatic unless the tracker asks for something specific."
        >
          <SelectInput
            value={String(settings.pieceLengthKiB ?? 0)}
            onChange={(value: string) => updateNow("pieceLengthKiB", Number(value) || 0)}
            options={PIECE_SIZE_OPTIONS}
          />
        </Hinted>

        <Hinted label="Torrent comment" hint="Optional free text embedded in the .torrent.">
          <TextInput value={settings.torrentComment} onChange={(value: string) => update("torrentComment", value)} />
        </Hinted>
      </section>

      <section>
        <h3>Output</h3>
        <Hinted label="Output directory" hint={`Blank uses the extension data directory. Currently: ${meta?.resolvedOutputDirectory ?? "—"}`}>
          <TextInput value={settings.outputDirectory} onChange={(value: string) => update("outputDirectory", value)} />
        </Hinted>

        <Hinted
          label="Pack folder"
          hint="Where multi-video packs are assembled and seeded from. Each pack gets a folder here holding hard links to its videos (the same files under a second name — no extra space, nothing in your library moves) plus its Screens folder. Must be on the same drive as your videos and outside every Cove library path."
        >
          <TextInput value={settings.packDirectory ?? ""} onChange={(value: string) => update("packDirectory", value)}
            placeholder="e.g. E:\Packs" />
        </Hinted>
        {meta?.packDirectoryProblem ? <p className="empprep-hint empprep-warn">{meta.packDirectoryProblem}</p> : null}

        <Hinted
          label="Single videos"
          hint="Off: a single video's torrent is the file itself, hashed where it sits in your library. On: it is hard-linked into a release-named folder in the pack folder instead."
        >
          <label className="empprep-check">
            <input type="checkbox" checked={!!settings.useHardlinkStaging}
              onChange={(event) => updateNow("useHardlinkStaging", event.target.checked)} />
            Build single-video torrents as a folder in the pack folder
          </label>
        </Hinted>
      </section>

      <section>
        <h3>Screenshots</h3>
        <p className="empprep-hint">
          {meta?.ffmpegPath ? `Using ffmpeg at ${meta.ffmpegPath}` : "ffmpeg was not found — screenshot generation will fail until Cove has a valid ffmpeg path."}
        </p>

        <Hinted label="Seconds per frame" hint="Target spacing. At 90s a 30-minute video yields 20 frames.">
          <NumberInput value={settings.secondsPerFrame} onChange={(value: number | undefined) => update("secondsPerFrame", value ?? 0)} />
        </Hinted>

        <Hinted label="Minimum frames" hint="Floor on the count regardless of spacing, so short videos still get a full grid.">
          <NumberInput value={settings.minFrameCount} onChange={(value: number | undefined) => update("minFrameCount", value ?? 0)} />
        </Hinted>

        <Hinted label="Maximum frames" hint="Upper bound so a multi-hour file does not produce hundreds of stills.">
          <NumberInput value={settings.maxFrameCount} onChange={(value: number | undefined) => update("maxFrameCount", value ?? 0)} />
        </Hinted>

        <Hinted label="Minimum spacing (seconds)" hint="Frames are never closer than this. This is what makes a very short clip get fewer than the minimum count.">
          <NumberInput value={settings.minFrameSpacingSeconds} onChange={(value: number | undefined) => update("minFrameSpacingSeconds", value ?? 0)} />
        </Hinted>

        <Hinted label="Skip start (%)" hint="Trimmed from the beginning, avoiding studio intros.">
          <NumberInput value={settings.skipStartPercent} onChange={(value: number | undefined) => update("skipStartPercent", value ?? 0)} />
        </Hinted>

        <Hinted label="Skip end (%)" hint="Trimmed from the end, avoiding credits.">
          <NumberInput value={settings.skipEndPercent} onChange={(value: number | undefined) => update("skipEndPercent", value ?? 0)} />
        </Hinted>

        <Hinted label="Format">
          <SelectInput
            value={settings.screenshotFormat}
            onChange={(value: string) => updateNow("screenshotFormat", value)}
            options={[
              { value: "jpg", label: "JPEG" },
              { value: "png", label: "PNG (lossless, much larger)" },
            ]}
          />
        </Hinted>

        {settings.screenshotFormat === "jpg" ? (
          <Hinted label="JPEG quality" hint="1–100.">
            <NumberInput value={settings.jpegQuality} onChange={(value: number | undefined) => update("jpegQuality", value ?? 0)} />
          </Hinted>
        ) : null}

        <Hinted label="Screenshot width" hint="0 keeps the source resolution, which is what a tracker wants — screens should show the actual encode.">
          <NumberInput value={settings.screenshotWidth} onChange={(value: number | undefined) => update("screenshotWidth", value ?? 0)} />
        </Hinted>
      </section>

      <section>
        <h3>hamsterimg.net</h3>
        <p className="empprep-hint">
          Images are uploaded to hamsterimg.net, flagged as adult content. Uploading publishes them, so
          it only happens when you press <strong>Upload to hamsterimg</strong> on an item.
        </p>

        <Hinted label="API key" hint="From hamsterimg.net → Settings → API. Required before uploading.">
          <TextInput value={settings.hamsterImgApiKey} onChange={(value: string) => update("hamsterImgApiKey", value)} />
        </Hinted>

        <Hinted label="Album id" hint="Optional. File uploads under one of your albums.">
          <TextInput value={settings.hamsterImgAlbumId} onChange={(value: string) => update("hamsterImgAlbumId", value)} />
        </Hinted>

        {meta?.imageHostProblem ? <p className="empprep-hint empprep-warn">{meta.imageHostProblem}</p> : null}
      </section>

      <section>
        <h3>Uploaded images</h3>
        <p className="empprep-hint">
          Each upload carries a few full-size images plus one contact sheet of every frame as the screens
          section. The individual frames are not uploaded; you can star one on an item to use it as a
          full-size image instead.
        </p>

        <Hinted
          label="Full-size images per video"
          hint="The Cove cover counts as one when it is used; frames spread through the video make up the rest."
        >
          <NumberInput value={settings.fullSizeImageCount}
            onChange={(value: number | undefined) => update("fullSizeImageCount", value ?? 2)} />
        </Hinted>

        <Hinted label="Cover image">
          <label className="empprep-check">
            <input type="checkbox" checked={!!settings.includeCoverImage}
              onChange={(event) => updateNow("includeCoverImage", event.target.checked)} />
            Use the video&apos;s cover art from Cove as the first full-size image
          </label>
        </Hinted>

        <Hinted label="Contact sheet columns">
          <NumberInput value={settings.contactSheetColumns}
            onChange={(value: number | undefined) => update("contactSheetColumns", value ?? 4)} />
        </Hinted>
        <Hinted label="Contact sheet tile width (px)" hint="Width of each frame inside the sheet.">
          <NumberInput value={settings.contactSheetTileWidth}
            onChange={(value: number | undefined) => update("contactSheetTileWidth", value ?? 480)} />
        </Hinted>

        <Hinted label="Contact sheet timestamps">
          <label className="empprep-check">
            <input type="checkbox" checked={settings.contactSheetTimestamps !== false}
              onChange={(event) => updateNow("contactSheetTimestamps", event.target.checked)} />
            Show each frame&apos;s timestamp in the corner of its tile
          </label>
          <label className="empprep-check">
            <input type="checkbox" checked={settings.contactSheetHeader !== false}
              onChange={(event) => updateNow("contactSheetHeader", event.target.checked)} />
            Show the file name and resolution across the top of the sheet
          </label>
        </Hinted>
      </section>

      <section>
        <h3>Tags &amp; templates</h3>
        <Hinted label="Tag handling">
          <label className="empprep-check">
            <input type="checkbox" checked={!!settings.lowercaseTags}
              onChange={(event) => updateNow("lowercaseTags", event.target.checked)} />
            Lowercase all tags
          </label>
          <label className="empprep-check">
            <input type="checkbox" checked={!!settings.includePerformerTags}
              onChange={(event) => updateNow("includePerformerTags", event.target.checked)} />
            Include performer names as tags
          </label>
          <label className="empprep-check">
            <input type="checkbox" checked={!!settings.includeStudioWebsiteTag}
              onChange={(event) => updateNow("includeStudioWebsiteTag", event.target.checked)} />
            Include the studio&apos;s website as a tag (e.g. pornhub.com)
          </label>
          <label className="empprep-check">
            <input type="checkbox" checked={!!settings.includeStudioTags}
              onChange={(event) => updateNow("includeStudioTags", event.target.checked)} />
            Include the studio name as a tag
          </label>
          <label className="empprep-check">
            <input type="checkbox" checked={!!settings.includeQualityTag}
              onChange={(event) => updateNow("includeQualityTag", event.target.checked)} />
            Include the quality as a tag (e.g. 1080p)
          </label>
          <label className="empprep-check">
            <input type="checkbox" checked={!!settings.includeDateTags}
              onChange={(event) => updateNow("includeDateTags", event.target.checked)} />
            Include the release date as tags (2024, 2024.05, 2024.05.01)
          </label>
          <label className="empprep-check">
            <input type="checkbox" checked={!!settings.includeCodecTag}
              onChange={(event) => updateNow("includeCodecTag", event.target.checked)} />
            Include the video codec as a tag (e.g. h264)
          </label>
        </Hinted>

        <Hinted
          label="Pack tags"
          hint="A pack's Tags field gets every performer, source site and quality, plus content tags carried by at least this share of its scenes. The description lists every tag with its count regardless."
        >
          <NumberInput value={settings.packTagThresholdPercent}
            onChange={(value: number | undefined) => update("packTagThresholdPercent", value ?? 25)} />
          <label className="empprep-check">
            <input type="checkbox" checked={settings.includePackTypeTag !== false}
              onChange={(event) => updateNow("includePackTypeTag", event.target.checked)} />
            Tag packs as megapack or minipack
          </label>
        </Hinted>

        <Hinted
          label="Pack title template"
          hint="Placeholders: {studio} (only when all scenes share one) {performers} (up to three, else Various) {packtype} {count} {quality}"
        >
          <TextInput value={settings.packTitleTemplate} onChange={(value: string) => update("packTitleTemplate", value)} />
        </Hinted>

        <Hinted
          label="Pack description template"
          hint="Placeholders: {cover} {theme} {packtitle} {count} {packtype} {runtime} {size} {stats} {performers} {sources} {tags} {screens} {screensnote}"
        >
          <TextArea rows={14} value={settings.packDescriptionTemplate}
            onChange={(value: string) => update("packDescriptionTemplate", value)} />
        </Hinted>

        <Hinted label="Excluded tags" hint="Comma separated. Matched after dotting, so organisational tags can be kept out of uploads.">
          <TextInput value={settings.excludedTags} onChange={(value: string) => update("excludedTags", value)} />
        </Hinted>

        <Hinted
          label="EMP title template"
          hint="Placeholders: {studio} {performers} {title} {date} {quality} {resolution} {height} {codec}. Empty brackets and dangling dashes are removed when a value is missing."
        >
          <TextInput value={settings.postTitleTemplate} onChange={(value: string) => update("postTitleTemplate", value)} />
        </Hinted>

        <Hinted
          label="Release name template"
          hint="Placeholders: {studio} {performers} {title} {date} {resolution} {height} {codec} {source}"
        >
          <TextInput value={settings.releaseNameTemplate} onChange={(value: string) => update("releaseNameTemplate", value)} />
        </Hinted>

        <Hinted label="BBCode columns" hint="Screenshots per row in the generated description.">
          <NumberInput value={settings.bbcodeColumns} onChange={(value: number | undefined) => update("bbcodeColumns", value ?? 0)} />
        </Hinted>

        <Hinted
          label="Description template"
          hint="Placeholders: {title} {studio} {performers} {date} {details} {tags} {mediainfo} {featured} {screenshots} {cover} {posttitle} {quality} {duration} {resolution} {filename} {filesize} {code} {director} {releasename}"
        >
          <TextArea rows={16} value={settings.descriptionTemplate}
            onChange={(value: string) => update("descriptionTemplate", value)} />
        </Hinted>
      </section>

    </div>
  );
}

export default {
  components: {
    EmpPrepPage,
    EmpPrepSettingsPanel,
  },
};
