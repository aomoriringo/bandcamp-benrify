import { ping, send, looksLikeBandcamp, kindOf } from "./api.js";

const $ = (id) => document.getElementById(id);

const LABELS = {
  album: "このアルバムの全曲をキューに追加",
  track: "この曲をキューに追加",
  band: "このレーベル / アーティストの全曲をキューに追加",
};

async function main() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const url = tab?.url ?? "";
  $("url").textContent = url;

  const alive = await ping();
  $("status").textContent = alive ? "アプリに接続しました" : "アプリが起動していません";
  $("status").className = alive ? "ok" : "ng";

  if (!looksLikeBandcamp(url)) {
    $("result").textContent = "Bandcamp のページで使ってください。";
    return;
  }

  $("enqueue").textContent = LABELS[kindOf(url)];
  $("enqueue").disabled = !alive;
  $("enqueue").addEventListener("click", async () => {
    $("enqueue").disabled = true;
    try {
      await send(url);
      $("result").textContent = "送信しました。アプリ側で順番に取得します。";
    } catch (e) {
      $("result").textContent = e.message;
    } finally {
      $("enqueue").disabled = false;
    }
  });
}

main();
