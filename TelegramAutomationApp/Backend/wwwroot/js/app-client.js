document.addEventListener("DOMContentLoaded", () => {
  // Determine API & SignalR Base URL (Supports both http://localhost:5000 and direct file:// open)
  const API_BASE = window.location.protocol === "file:" || window.location.port !== "5000" 
    ? "http://localhost:5000" 
    : "";

  // Global State
  let activeCampaignId = null;
  let currentUploadedImagePath = null;
  let registeredAccountsCount = 0;
  let campaignPollInterval = null;

  // In-App Toast Notification System
  function showToast(message, type = "info", title = null) {
    const container = document.getElementById("toastContainer");
    if (!container) {
      console.log(`[Toast ${type}]: ${message}`);
      return;
    }

    const icons = {
      success: "✓",
      error: "✕",
      warn: "⚠️",
      info: "ℹ️"
    };

    const defaultTitles = {
      success: "Success",
      error: "Notice",
      warn: "Warning",
      info: "Notification"
    };

    const toast = document.createElement("div");
    toast.className = `toast toast-${type}`;
    
    toast.innerHTML = `
      <div class="toast-icon">${icons[type] || "ℹ️"}</div>
      <div class="toast-content">
        <div class="toast-title">${escapeHtml(title || defaultTitles[type] || "Notification")}</div>
        <div class="toast-message">${escapeHtml(message)}</div>
      </div>
      <button type="button" class="toast-close" title="Dismiss">✕</button>
      <div class="toast-progress"></div>
    `;

    container.appendChild(toast);

    requestAnimationFrame(() => {
      toast.classList.add("show");
    });

    const closeBtn = toast.querySelector(".toast-close");
    let autoDismissTimer;

    function dismiss() {
      clearTimeout(autoDismissTimer);
      toast.classList.remove("show");
      toast.classList.add("hide");
      setTimeout(() => {
        if (toast.parentElement) {
          toast.parentElement.removeChild(toast);
        }
      }, 350);
    }

    closeBtn?.addEventListener("click", dismiss);
    autoDismissTimer = setTimeout(dismiss, 5000);
  }

  // SignalR Hub Setup
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${API_BASE}/hubs/automation`)
    .withAutomaticReconnect()
    .build();

  const signalrStatusEl = document.getElementById("signalrStatus");
  const terminalConsoleEl = document.getElementById("terminalConsole");
  const activityFeedBody = document.getElementById("activityFeedBody");

  connection.on("ReceiveLog", (log) => {
    appendLog(log.timestamp || log.Timestamp, log.level || log.Level, log.message || log.Message, log.accountPhone || log.AccountPhone);
  });

  connection.on("ReceiveProgress", (progress) => {
    updateProgressUI(progress);
  });

  connection.on("ReceiveRecipientStatus", (data) => {
    updateProgressUI(data);
    appendRecipientActivityRow(data);
  });

  async function startSignalR() {
    try {
      await connection.start();
      if (signalrStatusEl) {
        signalrStatusEl.textContent = "SignalR: Connected";
        signalrStatusEl.parentElement.style.color = "var(--accent-green)";
      }
      appendLog("System", "INFO", "SignalR WebSocket connected successfully.");
    } catch (err) {
      if (signalrStatusEl) {
        signalrStatusEl.textContent = "SignalR: Reconnecting...";
        signalrStatusEl.parentElement.style.color = "var(--accent-amber)";
      }
      setTimeout(startSignalR, 3000);
    }
  }
  startSignalR();

  // Helper Log Appender
  function appendLog(time, level, message, phone) {
    if (!terminalConsoleEl) return;
    
    const entry = document.createElement("div");
    entry.className = "log-entry";
    
    const phoneBadge = phone ? `<span class="log-phone">[${phone}]</span>` : "";
    
    entry.innerHTML = `
      <span class="log-time">[${time || new Date().toLocaleTimeString()}]</span>
      <span class="log-level ${level}">${level}</span>
      ${phoneBadge}
      <span class="log-msg">${escapeHtml(message)}</span>
    `;

    terminalConsoleEl.appendChild(entry);
    terminalConsoleEl.scrollTop = terminalConsoleEl.scrollHeight;
  }

  // Recipient Activity Feed Row Appender
  function appendRecipientActivityRow(data) {
    if (!activityFeedBody || !data) return;

    const targetUser = data.targetUsername ?? data.TargetUsername ?? "Unknown";
    const statusVal = data.status ?? data.Status ?? "Pending";
    const phoneVal = data.accountPhone ?? data.AccountPhone ?? "-";
    const timeVal = data.timestamp ?? data.Timestamp ?? new Date().toLocaleTimeString();
    const errorVal = data.errorMessage ?? data.ErrorMessage ?? "";

    if (activityFeedBody.children.length === 1 && activityFeedBody.children[0].cells.length === 1) {
      activityFeedBody.innerHTML = "";
    }

    const tr = document.createElement("tr");
    tr.className = `status-${statusVal}`;
    tr.innerHTML = `
      <td>${timeVal}</td>
      <td><strong>@${escapeHtml(targetUser)}</strong></td>
      <td><span class="account-tag ${statusVal === 'Success' ? 'tag-ready' : 'tag-cooldown'}">${statusVal}</span></td>
      <td>${phoneVal}</td>
      <td>${errorVal ? escapeHtml(errorVal) : 'Success'}</td>
    `;

    activityFeedBody.insertBefore(tr, activityFeedBody.firstChild);

    if (activityFeedBody.children.length > 50) {
      activityFeedBody.removeChild(activityFeedBody.lastChild);
    }
  }

  function escapeHtml(text) {
    if (!text) return "";
    return text
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;");
  }

  // Clear Logs
  document.getElementById("clearLogsBtn")?.addEventListener("click", () => {
    if (terminalConsoleEl) terminalConsoleEl.innerHTML = "";
  });

  // Tab Switcher
  const tabBtns = document.querySelectorAll(".tab-btn");
  const toolCards = document.querySelectorAll(".tool-card");

  tabBtns.forEach(btn => {
    btn.addEventListener("click", () => {
      tabBtns.forEach(b => b.classList.remove("active"));
      toolCards.forEach(c => c.classList.remove("active"));

      btn.classList.add("active");
      const targetTabId = btn.getAttribute("data-tab");
      document.getElementById(targetTabId)?.classList.add("active");
    });
  });

  // Live Telegram Chat Bubble Sync
  const msgTemplateInput = document.getElementById("msgTemplateInput");
  const bubbleText = document.getElementById("bubbleText");
  const bubbleTime = document.getElementById("bubbleTime");
  const insertUsernameTag = document.getElementById("insertUsernameTag");

  function updateBubbleText() {
    if (!bubbleText || !msgTemplateInput) return;
    const text = msgTemplateInput.value.trim();
    bubbleText.textContent = text || "Hello @username! Check out our special update here: https://example.com";
    
    if (bubbleTime) {
      const now = new Date();
      bubbleTime.textContent = now.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    }
  }

  msgTemplateInput?.addEventListener("input", updateBubbleText);
  updateBubbleText();

  insertUsernameTag?.addEventListener("click", (e) => {
    e.preventDefault();
    if (!msgTemplateInput) return;
    const start = msgTemplateInput.selectionStart;
    const end = msgTemplateInput.selectionEnd;
    const text = msgTemplateInput.value;
    msgTemplateInput.value = text.substring(0, start) + "{username}" + text.substring(end);
    msgTemplateInput.focus();
    updateBubbleText();
  });

  // Image Upload Attachment Handling
  const triggerImageUploadBtn = document.getElementById("triggerImageUploadBtn");
  const msgImageInput = document.getElementById("msgImageInput");
  const imageThumb = document.getElementById("imageThumb");
  const bubbleImage = document.getElementById("bubbleImage");
  const imageFileName = document.getElementById("imageFileName");
  const clearImageBtn = document.getElementById("clearImageBtn");

  triggerImageUploadBtn?.addEventListener("click", () => msgImageInput?.click());

  msgImageInput?.addEventListener("change", async () => {
    const file = msgImageInput.files[0];
    if (!file) return;

    const formData = new FormData();
    formData.append("file", file);

    try {
      imageFileName.textContent = "Uploading photo...";
      const res = await fetch(`${API_BASE}/api/campaigns/upload-image`, {
        method: "POST",
        body: formData
      });
      const data = await res.json();
      if (!res.ok) throw new Error(data.error || "Upload failed");

      currentUploadedImagePath = data.imageUrl;
      imageFileName.textContent = file.name;

      const fullImageUrl = `${API_BASE}${data.imageUrl}`;
      if (imageThumb) {
        imageThumb.src = fullImageUrl;
        imageThumb.style.display = "block";
      }
      if (bubbleImage) {
        bubbleImage.src = fullImageUrl;
        bubbleImage.style.display = "block";
      }
      if (clearImageBtn) {
        clearImageBtn.style.display = "inline-flex";
      }
      showToast("Photo attached successfully!", "success");
    } catch (err) {
      showToast("Image upload failed: " + err.message, "error");
      imageFileName.textContent = "No photo selected";
    }
  });

  clearImageBtn?.addEventListener("click", () => {
    currentUploadedImagePath = null;
    if (msgImageInput) msgImageInput.value = "";
    if (imageFileName) imageFileName.textContent = "No photo selected";
    if (imageThumb) imageThumb.style.display = "none";
    if (bubbleImage) bubbleImage.style.display = "none";
    if (clearImageBtn) clearImageBtn.style.display = "none";
    showToast("Attached photo removed.", "info");
  });

  // CSV & Excel File Upload Dropzones
  setupFileDropzone("adderFileDropzone", "adderFileInput", "adderUsernamesInput");
  setupFileDropzone("msgFileDropzone", "msgFileInput", "msgUsernamesInput");

  function setupFileDropzone(dropzoneId, inputId, targetTextareaId) {
    const dropzone = document.getElementById(dropzoneId);
    const input = document.getElementById(inputId);
    const textarea = document.getElementById(targetTextareaId);

    if (!dropzone || !input || !textarea) return;

    dropzone.addEventListener("click", () => input.click());

    dropzone.addEventListener("dragover", (e) => {
      e.preventDefault();
      dropzone.classList.add("dragover");
    });

    dropzone.addEventListener("dragleave", () => {
      dropzone.classList.remove("dragover");
    });

    dropzone.addEventListener("drop", (e) => {
      e.preventDefault();
      dropzone.classList.remove("dragover");
      if (e.dataTransfer.files.length > 0) {
        input.files = e.dataTransfer.files;
        handleFileParse(input.files[0], textarea);
      }
    });

    input.addEventListener("change", () => {
      if (input.files.length > 0) {
        handleFileParse(input.files[0], textarea);
      }
    });
  }

  async function handleFileParse(file, textarea) {
    const formData = new FormData();
    formData.append("file", file);

    try {
      appendLog("System", "INFO", `Parsing file: ${file.name}...`);
      const res = await fetch(`${API_BASE}/api/campaigns/parse-file`, {
        method: "POST",
        body: formData
      });
      const data = await res.json();
      if (!res.ok) throw new Error(data.error || "File parsing failed");

      if (data.targets && data.targets.length > 0) {
        textarea.value = data.targets.join("\n");
        appendLog("System", "SUCCESS", `Extracted ${data.targets.length} targets from ${file.name}`);
        showToast(`Imported ${data.targets.length} target users from ${file.name}!`, "success", "Import Successful");
      } else {
        showToast("No valid usernames or contacts found in file.", "warn");
      }
    } catch (err) {
      showToast("Error reading file: " + err.message, "error");
    }
  }

  // Fetch Accounts List
  async function loadAccounts() {
    try {
      const res = await fetch(`${API_BASE}/api/accounts`);
      if (!res.ok) return;
      const accounts = await res.json();
      
      registeredAccountsCount = accounts.length;

      const badge = document.getElementById("accountCountBadge");
      if (badge) badge.textContent = `(${accounts.length})`;

      const listEl = document.getElementById("accountsList");
      if (!listEl) return;

      if (accounts.length === 0) {
        listEl.innerHTML = `
          <div class="account-card" style="text-align: center; color: var(--text-muted); padding: 1.5rem 0;">
            No accounts registered.<br>Click "+" to add an account.
          </div>
        `;
        return;
      }

      listEl.innerHTML = accounts.map(acc => `
        <div class="account-card">
          <div class="account-info">
            <span class="account-phone">${acc.phoneNumber}</span>
            <span class="account-meta">API ID: ${acc.apiId}</span>
          </div>
          <div style="display: flex; align-items: center; gap: 0.5rem;">
            ${acc.isOnCooldown 
              ? `<span class="account-tag tag-cooldown">Cooldown</span>` 
              : `<span class="account-tag tag-ready">Ready</span>`}
            <button type="button" class="btn btn-danger btn-icon" onclick="deleteAccount(${acc.id})" title="Delete Account" style="font-size: 0.75rem;">✕</button>
          </div>
        </div>
      `).join("");

    } catch (err) {
      console.error("Failed loading accounts:", err);
    }
  }
  loadAccounts();

  // Delete Account
  window.deleteAccount = async function(id) {
    if (!confirm("Are you sure you want to delete this account?")) return;
    try {
      const res = await fetch(`${API_BASE}/api/accounts/${id}`, { method: "DELETE" });
      if (res.ok) {
        loadAccounts();
        appendLog("System", "INFO", `Account ID ${id} deleted.`);
        showToast(`Account ID ${id} removed successfully.`, "info");
      }
    } catch (err) {
      showToast("Failed to delete account: " + err.message, "error");
    }
  };

  // Modal Auth Logic
  const addAccountModal = document.getElementById("addAccountModal");
  const openAddAccountBtn = document.getElementById("openAddAccountBtn");
  const closeAddAccountBtn = document.getElementById("closeAddAccountBtn");

  openAddAccountBtn?.addEventListener("click", () => {
    showAuthStep(1);
    addAccountModal?.classList.add("active");
  });

  closeAddAccountBtn?.addEventListener("click", () => {
    addAccountModal?.classList.remove("active");
  });

  function showAuthStep(step) {
    document.getElementById("authStep1").style.display = step === 1 ? "block" : "none";
    document.getElementById("authStep2").style.display = step === 2 ? "block" : "none";
    document.getElementById("authStep3").style.display = step === 3 ? "block" : "none";
  }

  let currentPhone = "";

  // Step 1: Request OTP
  document.getElementById("requestOtpBtn")?.addEventListener("click", async () => {
    const phone = document.getElementById("phoneInput").value.trim();
    const apiId = parseInt(document.getElementById("apiIdInput").value);
    const apiHash = document.getElementById("apiHashInput").value.trim();

    if (!phone || !apiId || !apiHash) {
      showToast("Please fill in Phone, API ID, and API Hash.", "warn");
      return;
    }

    currentPhone = phone;

    try {
      const res = await fetch(`${API_BASE}/api/accounts/login-request`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ phoneNumber: phone, apiId: apiId, apiHash: apiHash })
      });
      const data = await res.json();
      if (!res.ok) {
        showToast(data.error || "Login request failed", "error");
        return;
      }

      if (data.authState === "AUTHORIZED") {
        showToast("Account is already authorized!", "success");
        addAccountModal?.classList.remove("active");
        loadAccounts();
      } else {
        showAuthStep(2);
        showToast("Verification code sent to your Telegram app.", "info");
      }
    } catch (err) {
      showToast(err.message, "error");
    }
  });

  // Step 2: Submit OTP
  document.getElementById("submitOtpBtn")?.addEventListener("click", async () => {
    const code = document.getElementById("otpCodeInput").value.trim();
    if (!code) {
      showToast("Please enter the verification code.", "warn");
      return;
    }

    try {
      const res = await fetch(`${API_BASE}/api/accounts/verify-code`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ phoneNumber: currentPhone, code: code })
      });
      const data = await res.json();
      if (!res.ok) {
        showToast(data.error || "Verification failed", "error");
        return;
      }

      if (data.authState === "AUTHORIZED") {
        showToast("Account authorized successfully!", "success");
        addAccountModal?.classList.remove("active");
        loadAccounts();
      } else if (data.authState === "password") {
        showAuthStep(3);
        showToast("Please enter your 2FA password.", "info");
      }
    } catch (err) {
      showToast(err.message, "error");
    }
  });

  // Step 3: Submit 2FA
  document.getElementById("submit2FaBtn")?.addEventListener("click", async () => {
    const password = document.getElementById("twoFactorPassInput").value;
    if (!password) {
      showToast("Please enter 2FA password.", "warn");
      return;
    }

    try {
      const res = await fetch(`${API_BASE}/api/accounts/verify-2fa`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ phoneNumber: currentPhone, password: password })
      });
      const data = await res.json();
      if (!res.ok) {
        showToast(data.error || "2FA verification failed", "error");
        return;
      }

      showToast("Account authorized successfully with 2FA!", "success");
      addAccountModal?.classList.remove("active");
      loadAccounts();
    } catch (err) {
      showToast(err.message, "error");
    }
  });

  // Start Group Member Adder Campaign
  const startAdderBtn = document.getElementById("startAdderBtn");
  startAdderBtn?.addEventListener("click", async () => {
    const group = document.getElementById("adderGroupInput").value.trim();
    const usernamesRaw = document.getElementById("adderUsernamesInput").value.trim();

    if (!group) {
      showToast("Please enter target group username or link.", "warn");
      return;
    }

    if (!usernamesRaw) {
      showToast("Please enter at least one target username.", "warn");
      return;
    }

    if (registeredAccountsCount === 0) {
      showToast("No Telegram accounts registered. Please click '+' on the left sidebar to add an account.", "warn", "Account Required");
      return;
    }

    const usernames = usernamesRaw
      .split(/[\n\r,;\s]+/)
      .map(s => s.trim().replace(/^@/, ''))
      .filter(s => s.length > 0);

    const originalBtnText = startAdderBtn.textContent;
    startAdderBtn.disabled = true;
    startAdderBtn.textContent = "⏳ Launching Campaign...";

    try {
      if (activityFeedBody) activityFeedBody.innerHTML = "";

      const res = await fetch(`${API_BASE}/api/campaigns/member-adder`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: `Member Adder -> ${group}`,
          targetGroupUsername: group,
          usernames: usernames
        })
      });
      const data = await res.json();
      if (!res.ok) {
        const errorMsg = typeof data === 'object' ? (data.error || data.title || JSON.stringify(data)) : data;
        showToast(errorMsg || "Failed to start campaign", "error");
        return;
      }

      activeCampaignId = data.campaignId;
      resetCampaignCounters(group, usernames.length, "Member Adder");
      startPollingCampaignStatus(activeCampaignId);
      appendLog("System", "INFO", `Campaign #${data.campaignId} launched with ${usernames.length} targets.`);
      showToast(`Member Adder Campaign Launched! (${usernames.length} targets queued)`, "success", "Campaign Started");
    } catch (err) {
      showToast("Error starting campaign: " + err.message, "error");
    } finally {
      startAdderBtn.disabled = false;
      startAdderBtn.textContent = originalBtnText;
    }
  });

  // Start Direct Messaging Campaign
  const startMsgBtn = document.getElementById("startMsgBtn");
  startMsgBtn?.addEventListener("click", async () => {
    const usernamesRaw = document.getElementById("msgUsernamesInput").value.trim();
    const template = document.getElementById("msgTemplateInput").value.trim();

    if (!usernamesRaw) {
      showToast("Please enter at least one recipient username.", "warn");
      return;
    }

    if (!template) {
      showToast("Please enter the message template text.", "warn");
      return;
    }

    if (registeredAccountsCount === 0) {
      showToast("No Telegram accounts registered. Please click '+' on the left sidebar to add an account.", "warn", "Account Required");
      return;
    }

    const usernames = usernamesRaw
      .split(/[\n\r,;\s]+/)
      .map(s => s.trim().replace(/^@/, ''))
      .filter(s => s.length > 0);

    const originalBtnText = startMsgBtn.textContent;
    startMsgBtn.disabled = true;
    startMsgBtn.textContent = "⏳ Launching Campaign...";

    try {
      if (activityFeedBody) activityFeedBody.innerHTML = "";

      const res = await fetch(`${API_BASE}/api/campaigns/direct-messaging`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: `DM Campaign (${usernames.length} targets)`,
          messageTemplate: template,
          imagePath: currentUploadedImagePath,
          usernames: usernames
        })
      });
      const data = await res.json();
      if (!res.ok) {
        const errorMsg = typeof data === 'object' ? (data.error || data.title || JSON.stringify(data)) : data;
        showToast(errorMsg || "Failed to start campaign", "error");
        return;
      }

      activeCampaignId = data.campaignId;
      resetCampaignCounters(`DM Campaign #${data.campaignId}`, usernames.length, "Direct Messaging");
      startPollingCampaignStatus(activeCampaignId);
      appendLog("System", "INFO", `Campaign #${data.campaignId} launched with ${usernames.length} targets.`);
      showToast(`Direct Messaging Campaign Launched! (${usernames.length} targets queued)`, "success", "Campaign Started");
    } catch (err) {
      showToast("Error starting campaign: " + err.message, "error");
    } finally {
      startMsgBtn.disabled = false;
      startMsgBtn.textContent = originalBtnText;
    }
  });

  function resetCampaignCounters(title, totalCount, type) {
    document.getElementById("activeCampaignName").textContent = title;
    document.getElementById("statTotal").textContent = totalCount;
    document.getElementById("statProcessed").textContent = "0";
    document.getElementById("statSuccess").textContent = "0";
    document.getElementById("statFailed").textContent = "0";
    
    const fill = document.getElementById("progressBarFill");
    if (fill) fill.style.width = "0%";

    document.getElementById("pauseCampaignBtn").disabled = false;
    document.getElementById("resumeCampaignBtn").disabled = true;
  }

  function startPollingCampaignStatus(campaignId) {
    if (campaignPollInterval) clearInterval(campaignPollInterval);

    async function poll() {
      if (!activeCampaignId || activeCampaignId !== campaignId) {
        clearInterval(campaignPollInterval);
        return;
      }

      try {
        const res = await fetch(`${API_BASE}/api/campaigns/${campaignId}`);
        if (!res.ok) return;
        const data = await res.json();
        updateProgressUI(data);

        if (data.status === "Completed" || data.status === "Failed") {
          clearInterval(campaignPollInterval);
        }
      } catch (err) {
        console.error("Poll status error:", err);
      }
    }

    poll();
    campaignPollInterval = setInterval(poll, 3000);
  }

  // Pause / Resume Actions
  document.getElementById("pauseCampaignBtn")?.addEventListener("click", async () => {
    if (!activeCampaignId) return;
    try {
      await fetch(`${API_BASE}/api/campaigns/${activeCampaignId}/pause`, { method: "POST" });
      document.getElementById("pauseCampaignBtn").disabled = true;
      document.getElementById("resumeCampaignBtn").disabled = false;
      appendLog("System", "WARN", `Campaign #${activeCampaignId} paused.`);
      showToast(`Campaign #${activeCampaignId} paused.`, "warn", "Paused");
    } catch (err) {
      showToast("Failed to pause campaign: " + err.message, "error");
    }
  });

  document.getElementById("resumeCampaignBtn")?.addEventListener("click", async () => {
    if (!activeCampaignId) return;
    try {
      await fetch(`${API_BASE}/api/campaigns/${activeCampaignId}/resume`, { method: "POST" });
      document.getElementById("pauseCampaignBtn").disabled = false;
      document.getElementById("resumeCampaignBtn").disabled = true;
      startPollingCampaignStatus(activeCampaignId);
      appendLog("System", "INFO", `Campaign #${activeCampaignId} resumed.`);
      showToast(`Campaign #${activeCampaignId} resumed.`, "info", "Resumed");
    } catch (err) {
      showToast("Failed to resume campaign: " + err.message, "error");
    }
  });

  // Update Progress UI from SignalR Event or Polling DTO
  function updateProgressUI(p) {
    if (!p) return;

    const cid = p.campaignId ?? p.CampaignId ?? p.id ?? p.Id;
    if (activeCampaignId && cid && parseInt(cid) !== parseInt(activeCampaignId)) {
      return;
    }

    const total = p.total ?? p.Total ?? p.totalTargets ?? p.TotalTargets ?? 0;
    const processed = p.processed ?? p.Processed ?? p.processedTargets ?? p.ProcessedTargets ?? 0;
    const success = p.success ?? p.Success ?? p.successCount ?? p.SuccessCount ?? 0;
    const failed = p.failed ?? p.Failed ?? p.failedCount ?? p.FailedCount ?? 0;
    const percentage = p.percentage ?? p.Percentage ?? (total > 0 ? Math.round((processed / total) * 100) : 0);

    const elTotal = document.getElementById("statTotal");
    const elProcessed = document.getElementById("statProcessed");
    const elSuccess = document.getElementById("statSuccess");
    const elFailed = document.getElementById("statFailed");
    const fill = document.getElementById("progressBarFill");

    if (elTotal) elTotal.textContent = total;
    if (elProcessed) elProcessed.textContent = processed;
    if (elSuccess) elSuccess.textContent = success;
    if (elFailed) elFailed.textContent = failed;
    if (fill) fill.style.width = `${percentage}%`;

    const statusStr = (p.status ?? p.Status ?? "").toString();
    if (statusStr === "Completed" || statusStr === "Failed") {
      const pauseBtn = document.getElementById("pauseCampaignBtn");
      const resumeBtn = document.getElementById("resumeCampaignBtn");
      if (pauseBtn) pauseBtn.disabled = true;
      if (resumeBtn) resumeBtn.disabled = true;
    }
  }

});
