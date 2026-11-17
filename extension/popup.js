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
  const actions = {
    browse: "アプリの「探す」で開きました。曲を選んでキューに追加できます。",
    enqueue: "送信しました。アプリ側で順番に取得します。",
  };
  for (const [action, message] of Object.entries(actions)) {
    $(action).disabled = !alive;
    $(action).addEventListener("click", async () => {
      $(action).disabled = true;
      try {
        await send(url, action);
        $("result").textContent = message;
      } catch (e) {
        $("result").textContent = e.message;
      } finally {
        $(action).disabled = false;
      }
    });
  }
}

main();
