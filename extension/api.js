// Bandcamp Benrify (Windows アプリ) が localhost で待ち受けている API を呼ぶ。
// 拡張機能はこの localhost にしか通信せず、Bandcamp へのリクエストはアプリ側が間隔を空けて行う。
export const API_BASE = "http://localhost:47823";

export async function ping() {
  try {
    const res = await fetch(`${API_BASE}/ping`, { method: "GET" });
    return res.ok;
  } catch {
    return false;
  }
}

/** @param {"enqueue" | "ranking"} action */
export async function send(action, url) {
  let res;
  try {
    res = await fetch(`${API_BASE}/${action}`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Benrify": "1" },
      body: JSON.stringify({ url }),
    });
  } catch {
    throw new Error("アプリに接続できません。Bandcamp Benrify を起動してください。");
  }
  const json = await res.json().catch(() => ({}));
  if (!res.ok || !json.ok) {
    throw new Error(json.error || `HTTP ${res.status}`);
  }
}

export function looksLikeBandcamp(url) {
  try {
    const u = new URL(url);
    return u.hostname.endsWith(".bandcamp.com") || /\/(album|track)\//.test(u.pathname);
  } catch {
    return false;
  }
}

/** URL の種類 (アプリの判定と同じ規則) */
export function kindOf(url) {
  const path = new URL(url).pathname;
  if (path.includes("/album/")) return "album";
  if (path.includes("/track/")) return "track";
  return "band";
}
