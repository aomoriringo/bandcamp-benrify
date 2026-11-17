import { send } from "./api.js";

const BANDCAMP = ["*://*.bandcamp.com/*"];

const MENUS = [
  { id: "browse-link", action: "browse", contexts: ["link"], title: "Benrify の「探す」で開く" },
  { id: "enqueue-link", action: "enqueue", contexts: ["link"], title: "Benrify のキューに追加" },
  { id: "browse-page", action: "browse", contexts: ["page"], title: "このページを Benrify の「探す」で開く" },
  { id: "enqueue-page", action: "enqueue", contexts: ["page"], title: "このページを Benrify のキューに追加" },
];

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    for (const m of MENUS) {
      chrome.contextMenus.create({
        id: m.id,
        title: m.title,
        contexts: m.contexts,
        ...(m.contexts.includes("link") ? { targetUrlPatterns: BANDCAMP } : { documentUrlPatterns: BANDCAMP }),
      });
    }
  });
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const menu = MENUS.find((m) => m.id === info.menuItemId);
  if (!menu) return;
  const url = menu.contexts.includes("link") ? info.linkUrl : info.pageUrl;
  try {
    await send(url, menu.action);
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
