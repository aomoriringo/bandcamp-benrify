import { send } from "./api.js";

const BANDCAMP = ["*://*.bandcamp.com/*"];

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: "enqueue-link",
      title: "Benrify のキューに追加",
      contexts: ["link"],
      targetUrlPatterns: BANDCAMP,
    });
    chrome.contextMenus.create({
      id: "ranking-link",
      title: "Benrify でサポーター数ランキングを表示",
      contexts: ["link"],
      targetUrlPatterns: BANDCAMP,
    });
    chrome.contextMenus.create({
      id: "enqueue-page",
      title: "このページを Benrify のキューに追加",
      contexts: ["page"],
      documentUrlPatterns: BANDCAMP,
    });
    chrome.contextMenus.create({
      id: "ranking-page",
      title: "このレーベル / アーティストを Benrify でランキング表示",
      contexts: ["page"],
      documentUrlPatterns: BANDCAMP,
    });
  });
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const url = info.menuItemId.endsWith("-link") ? info.linkUrl : info.pageUrl;
  const action = info.menuItemId.startsWith("enqueue") ? "enqueue" : "ranking";
  try {
    await send(action, url);
    await flash(tab, "OK", "#1DA0C3");
  } catch (e) {
    console.warn(e);
    await flash(tab, "ERR", "#D64545", e.message);
  }
});

async function flash(tab, text, color, title) {
  const tabId = tab?.id;
  await chrome.action.setBadgeBackgroundColor({ color, tabId });
  await chrome.action.setBadgeText({ text, tabId });
  if (title) {
    await chrome.action.setTitle({ title: `Bandcamp Benrify: ${title}`, tabId });
  }
  setTimeout(() => {
    chrome.action.setBadgeText({ text: "", tabId });
    chrome.action.setTitle({ title: "Bandcamp Benrify", tabId });
  }, 3000);
}
