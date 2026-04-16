const MENU_ID = "read-with-rightspeak";
const HOST_NAME = "com.rightspeak.bridge";

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: MENU_ID,
      title: "Read with RightSpeak",
      contexts: ["selection"]
    });
  });
});

chrome.contextMenus.onClicked.addListener(async (info) => {
  if (info.menuItemId !== MENU_ID) {
    return;
  }

  const text = (info.selectionText || "").trim();
  if (!text) {
    return;
  }

  try {
    const response = await chrome.runtime.sendNativeMessage(HOST_NAME, { text });
    if (!response || response.success !== true) {
      console.error("RightSpeak native host rejected request.", response);
    }
  } catch (error) {
    console.error("Failed to send selected text to RightSpeak.", error);
  }
});
