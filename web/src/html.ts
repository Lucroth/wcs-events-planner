/** Markup that is already safe to insert. Anything else interpolated into `html` is escaped. */
export class Raw {
  constructor(readonly value: string) {}
  toString(): string {
    return this.value;
  }
}

const escapes: Record<string, string> = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" };

export const escape = (s: string): string => s.replace(/[&<>"']/g, (c) => escapes[c]);

function render(value: unknown): string {
  if (value instanceof Raw) return value.value;
  if (value === null || value === undefined || value === false) return "";
  if (Array.isArray(value)) return value.map(render).join("");
  return escape(String(value));
}

/** Template tag: escapes every interpolation except nested `html` results. */
export function html(strings: TemplateStringsArray, ...values: unknown[]): Raw {
  return new Raw(strings.reduce((out, s, i) => out + s + (i < values.length ? render(values[i]) : ""), ""));
}

/**
 * Only http(s) links reach an href: the admin types them, anonymous readers click them, and a
 * `javascript:` URL would run in the reader's session.
 */
export function safeUrl(url: string | null | undefined): string | null {
  if (!url) return null;
  try {
    const parsed = new URL(url.trim());
    return parsed.protocol === "http:" || parsed.protocol === "https:" ? parsed.href : null;
  } catch {
    return null;
  }
}
