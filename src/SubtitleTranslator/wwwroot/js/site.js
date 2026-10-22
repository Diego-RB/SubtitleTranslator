// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

(() => {
	const pollIntervalMilliseconds = 3000;
	const terminalStatuses = new Set(["completed", "failed", "cancelled"]);

	function setText(root, selector, value) {
		const element = root.querySelector(selector);
		if (element && value !== null && value !== undefined) element.textContent = value;
	}

	function setStatusClass(element, status) {
		if (!element) return;
		const normalized = String(status).toLowerCase();
		const className = normalized === "completed"
			? "text-bg-success"
			: normalized === "failed" || normalized === "cancelled"
				? "text-bg-danger"
				: normalized === "discovered" || normalized === "queued"
					? "text-bg-secondary"
					: "text-bg-primary";
		element.className = `badge ${className}`;
	}

	function formatCurrency(value) {
		return new Intl.NumberFormat(undefined, { style: "currency", currency: "USD" }).format(Number(value) || 0);
	}

	async function getJson(url) {
		const response = await fetch(url, {
			headers: { Accept: "application/json" },
			cache: "no-store",
			credentials: "same-origin"
		});
		if (!response.ok || !response.headers.get("content-type")?.includes("application/json")) {
			throw new Error(`Live update request failed (${response.status}).`);
		}
		return response.json();
	}

	function startPolling(root, update, shouldStop = () => false) {
		let inFlight = false;
		let timer;
		const poll = async () => {
			if (inFlight || document.visibilityState === "hidden") return;
			inFlight = true;
			try {
				const data = await getJson(root.dataset.liveUrl);
				update(data);
				if (shouldStop(data)) window.clearInterval(timer);
			} catch {
				// Keep the page usable during temporary network interruptions.
			} finally {
				inFlight = false;
			}
		};
		poll();
		timer = window.setInterval(poll, pollIntervalMilliseconds);
	}

	function updateDashboard(data) {
		const dashboard = document.querySelector("[data-live-dashboard]");
		if (!dashboard) return;
		for (const key of ["activeJobs", "queuedJobs", "completedToday", "failedJobs", "stalledJobs"]) {
			setText(dashboard, `[data-live-counter="${key}"]`, data[key]);
		}

		const worker = dashboard.querySelector("[data-live-worker]");
		if (worker) {
			worker.textContent = data.isWorkerOnline ? "WORKER ONLINE" : "WORKER OFFLINE";
			worker.classList.toggle("text-bg-success", data.isWorkerOnline);
			worker.classList.toggle("text-bg-danger", !data.isWorkerOnline);
		}

		for (const job of data.recentJobs ?? []) {
			const row = dashboard.querySelector(`[data-live-dashboard-job="${job.id}"]`);
			if (!row) continue;
			const status = row.querySelector("[data-live-job-status]");
			if (status) {
				status.textContent = job.status;
				setStatusClass(status, job.status);
			}
			setText(row, "[data-live-job-chunks]", job.progressLabel ?? `${job.completedChunks} / ${job.totalChunks} chunks`);
			setText(row, "[data-live-job-percent]", `${job.progressPercent}%`);
			setText(row, "[data-live-job-provider]", job.currentProvider ?? "—");
			setText(row, "[data-live-job-model]", job.currentModel ?? "");
			setText(row, "[data-live-job-cost]", formatCurrency(job.estimatedCost));
			const progress = row.querySelector("[data-live-job-progress]");
			if (progress) progress.style.width = `${job.progressPercent}%`;
			const container = row.querySelector('[role="progressbar"]');
			if (container) container.setAttribute("aria-valuenow", job.progressPercent);
		}
	}

	function formatRuntime(startedAt, endedAt) {
		if (!startedAt) return "Not started";
		const end = endedAt ?? new Date().toISOString();
		const seconds = Math.max(0, Math.floor((Date.parse(end) - Date.parse(startedAt)) / 1000));
		const hours = Math.floor(seconds / 3600).toString().padStart(2, "0");
		const minutes = Math.floor((seconds % 3600) / 60).toString().padStart(2, "0");
		const remainder = (seconds % 60).toString().padStart(2, "0");
		return `${hours}:${minutes}:${remainder}`;
	}

	function updateJobDetails(data) {
		const root = document.querySelector("[data-live-job]");
		if (!root) return;
		setText(root, "[data-live-status]", data.status);
		setText(root, "[data-live-progress-label]", data.progressLabel ?? `${data.completedChunks} / ${data.totalChunks} chunks`);
		setText(root, "[data-live-progress-percent]", `${data.progressPercent}%`);
		const endedAt = data.completedAt ?? (terminalStatuses.has(data.status.toLowerCase()) ? data.updatedAt : null);
		setText(root, "[data-live-runtime]", formatRuntime(data.startedAt, endedAt));
		setText(root, "[data-live-cost]", formatCurrency(data.estimatedCost));
		setText(root, "[data-live-input-tokens]", Number(data.inputTokens).toLocaleString());
		setText(root, "[data-live-output-tokens]", Number(data.outputTokens).toLocaleString());
		setText(root, "[data-live-retries]", data.retryCount);
		setText(root, "[data-live-batch-id]", data.azureBatchId ?? "—");
		setText(root, "[data-live-batch-status]", data.azureBatchStatus ?? "—");
		setText(root, "[data-live-batch-completed]", data.azureBatchCompletedRequests ?? "—");
		setText(root, "[data-live-batch-total]", data.azureBatchTotalRequests ?? "—");
		setText(root, "[data-live-batch-failed]", data.azureBatchFailedRequests ?? 0);
		setText(root, "[data-live-last-activity]", new Date(data.lastActivityAt ?? data.updatedAt).toLocaleString());

		const progress = root.querySelector("[data-live-progress-bar]");
		if (progress) progress.style.width = `${data.progressPercent}%`;
		const container = progress?.closest('[role="progressbar"]');
		if (container) container.setAttribute("aria-valuenow", data.progressPercent);
		const errorContainer = root.querySelector("[data-live-last-error-container]");
		const error = root.querySelector("[data-live-last-error]");
		if (errorContainer && error) {
			errorContainer.hidden = !data.lastError;
			error.textContent = data.lastError ?? "";
		}
		const retryControl = root.querySelector("[data-live-retry-control]");
		if (retryControl) retryControl.hidden = !["failed", "cancelled"].includes(data.status.toLowerCase());
	}

	function initializeChunkFilters() {
		const filters = document.querySelector("[data-chunk-filters]");
		if (!filters) return;

		const providerFilter = filters.querySelector("[data-chunk-provider-filter]");
		const modelFilter = filters.querySelector("[data-chunk-model-filter]");
		const errorsOnly = filters.querySelector("[data-chunk-errors-only]");
		const count = filters.querySelector("[data-chunk-filter-count]");
		const cards = [...document.querySelectorAll("[data-chunk-card]")];
		const noResults = document.querySelector("[data-chunk-no-results]");

		const applyFilters = () => {
			const provider = providerFilter.value.trim().toLocaleLowerCase();
			const model = modelFilter.value.trim().toLocaleLowerCase();
			const requireErrors = errorsOnly.checked;
			let visible = 0;

			for (const card of cards) {
				const matches = (!provider || card.dataset.chunkProvider?.toLocaleLowerCase() === provider)
					&& (!model || card.dataset.chunkModel?.toLocaleLowerCase() === model)
					&& (!requireErrors || card.dataset.chunkHasErrors === "true");
				card.hidden = !matches;
				if (matches) visible++;
			}

			if (count) count.textContent = `Showing ${visible} of ${cards.length} chunks`;
			if (noResults) noResults.hidden = cards.length === 0 || visible > 0;
		};

		providerFilter.addEventListener("change", applyFilters);
		modelFilter.addEventListener("change", applyFilters);
		errorsOnly.addEventListener("change", applyFilters);
		filters.querySelector("[data-chunk-filter-reset]").addEventListener("click", () => {
			providerFilter.value = "";
			modelFilter.value = "";
			errorsOnly.checked = false;
			applyFilters();
		});
		applyFilters();
	}

	const dashboard = document.querySelector("[data-live-dashboard]");
	if (dashboard) startPolling(dashboard, updateDashboard);
	const jobDetails = document.querySelector("[data-live-job]");
	if (jobDetails) startPolling(jobDetails, updateJobDetails, data => terminalStatuses.has(data.status.toLowerCase()));
	initializeChunkFilters();
})();
