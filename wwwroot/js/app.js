(() => {
  const PAGE_SIZE = 15;
  const GALLERY_BATCH = 15;
  const MAX_PAGE_BUTTONS = 3; // matches the reference's compact « 4 5 6 » style

  const state = {
    region: "en-US",
    seed: randomSeed(),
    likes: 2.0,
    reviews: 1.0,
    view: "table",
    tablePage: 1,
    galleryCursor: 0,
    galleryLoading: false,
  };

  // ---- element refs ---------------------------------------------------
  const el = {
    regionSelect: document.getElementById("regionSelect"),
    seedInput: document.getElementById("seedInput"),
    randomSeedBtn: document.getElementById("randomSeedBtn"),
    likesInput: document.getElementById("likesInput"),
    reviewsInput: document.getElementById("reviewsInput"),
    reviewsUpBtn: document.getElementById("reviewsUpBtn"),
    reviewsDownBtn: document.getElementById("reviewsDownBtn"),
    tableViewBtn: document.getElementById("tableViewBtn"),
    galleryViewBtn: document.getElementById("galleryViewBtn"),
    exportBtn: document.getElementById("exportBtn"),
    statusBar: document.getElementById("statusBar"),
    tableView: document.getElementById("tableView"),
    galleryView: document.getElementById("galleryView"),
    tableBody: document.getElementById("tableBody"),
    pager: document.getElementById("pager"),
    galleryGrid: document.getElementById("galleryGrid"),
    galleryLoader: document.getElementById("galleryLoader"),
    detailTemplate: document.getElementById("movieDetailTemplate"),
    galleryModalOverlay: document.getElementById("galleryModalOverlay"),
    galleryModalContent: document.getElementById("galleryModalContent"),
    galleryModalClose: document.getElementById("galleryModalClose"),
  };

  function randomSeed() {
    // 48-bit-ish range, safely within Number precision
    return Math.floor(Math.random() * 281474976710656);
  }

  function setStatus(msg) { el.statusBar.textContent = msg || ""; }

  // ---- init -------------------------------------------------------------
  async function init() {
    el.seedInput.value = state.seed;
    el.likesInput.value = state.likes;
    el.reviewsInput.value = state.reviews;

    const locales = await fetchJson("/api/locales");
    el.regionSelect.innerHTML = locales
      .map(l => `<option value="${l.code}">${escapeHtml(l.displayName)}</option>`)
      .join("");
    el.regionSelect.value = state.region;

    bindEvents();
    await loadTablePage(1);
  }

  function bindEvents() {
    el.regionSelect.addEventListener("change", () => {
      state.region = el.regionSelect.value;
      onParamsChanged();
    });

    el.seedInput.addEventListener("change", () => {
      const v = parseInt(el.seedInput.value, 10);
      state.seed = Number.isFinite(v) && v >= 0 ? v : randomSeed();
      el.seedInput.value = state.seed;
      onParamsChanged();
    });

    el.randomSeedBtn.addEventListener("click", () => {
      state.seed = randomSeed();
      el.seedInput.value = state.seed;
      onParamsChanged();
    });

    let likesDebounce;
    el.likesInput.addEventListener("input", () => {
      state.likes = parseFloat(el.likesInput.value);
      clearTimeout(likesDebounce);
      likesDebounce = setTimeout(onParamsChanged, 150);
    });

    function commitReviews(v) {
      if (!Number.isFinite(v)) v = 0;
      state.reviews = Math.round(Math.min(10, Math.max(0, v)) * 10) / 10;
      el.reviewsInput.value = state.reviews;
      onParamsChanged();
    }
    el.reviewsInput.addEventListener("change", () => commitReviews(parseFloat(el.reviewsInput.value)));
    el.reviewsUpBtn.addEventListener("click", () => commitReviews(state.reviews + 0.1));
    el.reviewsDownBtn.addEventListener("click", () => commitReviews(state.reviews - 0.1));

    el.tableViewBtn.addEventListener("click", () => switchView("table"));
    el.galleryViewBtn.addEventListener("click", () => switchView("gallery"));

    el.exportBtn.addEventListener("click", () => {
      const url = `/api/movies/export?region=${encodeURIComponent(state.region)}&seed=${state.seed}&page=${state.tablePage}&pageSize=${PAGE_SIZE}`;
      window.location.href = url;
    });

    const galleryObserver = new IntersectionObserver(entries => {
      if (entries[0].isIntersecting && state.view === "gallery" && !state.galleryLoading) {
        loadGalleryBatch();
      }
    }, { rootMargin: "300px" });
    galleryObserver.observe(el.galleryLoader);

    el.galleryModalClose.addEventListener("click", closeGalleryModal);
    el.galleryModalOverlay.addEventListener("click", (e) => {
      if (e.target === el.galleryModalOverlay) closeGalleryModal(); // backdrop click only
    });
    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && !el.galleryModalOverlay.classList.contains("hidden")) closeGalleryModal();
    });
  }

  // Any change to region/seed/likes/reviews: reset both views per spec.
  function onParamsChanged() {
    closeGalleryModal();
    state.tablePage = 1;
    state.galleryCursor = 0;
    el.galleryGrid.innerHTML = "";
    if (state.view === "table") {
      loadTablePage(1);
    } else {
      loadGalleryBatch();
    }
  }

  function switchView(view) {
    state.view = view;
    el.tableViewBtn.classList.toggle("active", view === "table");
    el.galleryViewBtn.classList.toggle("active", view === "gallery");
    el.tableView.classList.toggle("hidden", view !== "table");
    el.galleryView.classList.toggle("hidden", view !== "gallery");

    if (view === "gallery" && el.galleryGrid.children.length === 0) {
      state.galleryCursor = 0;
      loadGalleryBatch();
    }
  }

  // ---- Table view ---------------------------------------------------------
  async function loadTablePage(page) {
    setStatus("Loading...");
    try {
      const url = `/api/movies?region=${encodeURIComponent(state.region)}&seed=${state.seed}&page=${page}&pageSize=${PAGE_SIZE}&likes=${state.likes}&reviews=${state.reviews}`;
      const data = await fetchJson(url);
      state.tablePage = page;
      renderTable(data.items);
      renderPager(page);
      setStatus("");
      prefetchTrailers(data.items);
    } catch (e) {
      setStatus("Failed to load movies: " + e.message);
    }
  }

  // Warm the server-side trailer cache for the whole visible page in the
  // background (low concurrency so it doesn't hammer ffmpeg), so that by
  // the time the user actually opens a row/card and hits play, the mp4 is
  // already generated instead of them waiting for ffmpeg on first click.
  async function prefetchTrailers(movies) {
    // Kept low (1) so the background prefetch doesn't pile up parallel ffmpeg
    // encodes on the server - small hosts (Render's free tier, 512MB RAM) can
    // get OOM-killed if several run at once. The server also has its own
    // single-ffmpeg-at-a-time gate as a backstop either way.
    const CONCURRENCY = 1;
    let i = 0;
    async function worker() {
      while (i < movies.length) {
        const movie = movies[i++];
        try {
          await fetch(movie.trailerPosterUrl);
        } catch { /* best effort, ignore failures */ }
      }
    }
    await Promise.all(Array.from({ length: CONCURRENCY }, worker));
  }

  // Compact « 1 2 3 » pager with no known total page count (data is endless),
  // so "next" is always enabled and only a small window of page numbers
  // around the current page is shown - same shape as the reference's « 4 5 6 ».
  function renderPager(page) {
    el.pager.innerHTML = "";

    const prev = document.createElement("button");
    prev.textContent = "\u00ab";
    prev.disabled = page <= 1;
    prev.addEventListener("click", () => loadTablePage(page - 1));
    el.pager.appendChild(prev);

    const start = Math.max(1, page - 1);
    for (let p = start; p < start + MAX_PAGE_BUTTONS; p++) {
      const btn = document.createElement("button");
      btn.textContent = String(p);
      if (p === page) btn.classList.add("active");
      btn.addEventListener("click", () => loadTablePage(p));
      el.pager.appendChild(btn);
    }

    const next = document.createElement("button");
    next.textContent = "\u00bb";
    next.addEventListener("click", () => loadTablePage(page + 1));
    el.pager.appendChild(next);
  }

  function renderTable(movies) {
    el.tableBody.innerHTML = "";
    for (const movie of movies) {
      const row = document.createElement("tr");
      row.className = "movie-row";
      row.innerHTML = `
        <td><span class="caret">${caretSvg()}</span></td>
        <td>${movie.index}</td>
        <td>${escapeHtml(movie.genre)}</td>
        <td>${escapeHtml(movie.title)}</td>
        <td>${movie.cast.map(escapeHtml).join(", ")}</td>
        <td>${movie.year}</td>
      `;
      const detailRow = document.createElement("tr");
      detailRow.className = "detail-row";
      const detailCell = document.createElement("td");
      detailCell.colSpan = 6;
      detailRow.appendChild(detailCell);
      detailRow.style.display = "none";

      let built = false;
      row.addEventListener("click", () => {
        const isOpen = detailRow.style.display !== "none";
        detailRow.style.display = isOpen ? "none" : "table-row";
        row.classList.toggle("open", !isOpen);
        row.querySelector(".caret").classList.toggle("open", !isOpen);
        if (!built && !isOpen) {
          detailCell.appendChild(buildDetail(movie));
          built = true;
        }
      });

      el.tableBody.appendChild(row);
      el.tableBody.appendChild(detailRow);
    }
  }

  function caretSvg() {
    return `<svg viewBox="0 0 24 24" width="14" height="14"><path fill="currentColor" d="M7 10l5 5 5-5z"/></svg>`;
  }

  function buildDetail(movie) {
    const node = el.detailTemplate.content.cloneNode(true);

    const video = node.querySelector(".trailer-video");
    const overlay = node.querySelector(".play-overlay");
    video.poster = movie.trailerPosterUrl;
    video.src = movie.trailerUrl;
    overlay.addEventListener("click", () => video.play());
    video.addEventListener("play", () => overlay.classList.add("hidden"));
    video.addEventListener("pause", () => overlay.classList.remove("hidden"));

    node.querySelector(".trailer-wrap .likes-btn").innerHTML = `\u{1F44D} ${movie.likes}`;

    node.querySelector(".detail-title").textContent = movie.title;
    node.querySelector(".detail-yeargenre").textContent = `${movie.year}, ${movie.genre}`;

    const badges = node.querySelector(".detail-badges");
    const seriesTag = movie.isSeries
      ? `<span class="badge-top10">SERIES</span>`
      : `<span class="badge-top10">Top 10</span>`;
    badges.innerHTML = `${seriesTag}<span>${movie.durationMinutes} min</span><span class="badge-age">${escapeHtml(movie.ageRating)}</span>`;

    node.querySelector(".detail-cast").innerHTML =
      `Cast: <i>${movie.cast.map(escapeHtml).join(", ")}</i>`;
    node.querySelector(".detail-director").innerHTML =
      `Director: <i>${escapeHtml(movie.director)}</i>`;

    node.querySelector(".detail-desc").textContent = movie.description;

    if (movie.isSeries && movie.seasons.length > 0) {
      const seasonsEl = document.createElement("div");
      seasonsEl.className = "seasons";
      seasonsEl.innerHTML = movie.seasons
        .map(s => `<span class="season-chip">S${s.seasonNumber} \u00b7 ${s.episodeCount} ep</span>`)
        .join("");
      node.querySelector(".detail-desc").after(seasonsEl);
    }

    const reviewsEl = node.querySelector(".reviews");
    if (movie.reviews.length === 0) {
      node.querySelector(".review-header").style.display = "none";
    } else {
      for (const r of movie.reviews) {
        const div = document.createElement("div");
        div.className = "review";
        div.innerHTML = `<div class="review-text">${escapeHtml(r.text)}</div><div class="author">\u2014 ${escapeHtml(r.author)}, <i>${escapeHtml(r.company)}</i></div>`;
        reviewsEl.appendChild(div);
      }
    }
    return node;
  }

  // ---- Gallery view ---------------------------------------------------------
  async function loadGalleryBatch() {
    state.galleryLoading = true;
    el.galleryLoader.classList.remove("hidden");
    try {
      const url = `/api/movies/gallery?region=${encodeURIComponent(state.region)}&seed=${state.seed}&cursor=${state.galleryCursor}&batchSize=${GALLERY_BATCH}&likes=${state.likes}&reviews=${state.reviews}`;
      const data = await fetchJson(url);
      state.galleryCursor = data.nextCursor;
      for (const movie of data.items) el.galleryGrid.appendChild(buildGalleryCard(movie));
      prefetchTrailers(data.items);
    } catch (e) {
      setStatus("Failed to load more movies: " + e.message);
    } finally {
      state.galleryLoading = false;
    }
  }

  function buildGalleryCard(movie) {
    const card = document.createElement("div");
    card.className = "gallery-card";
    card.innerHTML = `
      <div class="poster" style="background:${movie.posterHex}">${escapeHtml(movie.title)}</div>
      <div class="info">
        <div class="title">#${movie.index} \u00b7 ${escapeHtml(movie.title)}${movie.isSeries ? " \u{1F4FA}" : ""}</div>
        <div class="meta">${escapeHtml(movie.genre)} \u00b7 ${movie.year} \u00b7 \u{1F44D} ${movie.likes}</div>
      </div>
    `;
    card.addEventListener("click", () => openGalleryModal(movie));
    return card;
  }

  // Gallery cards are ~240px wide - the movie-detail block (built for a
  // wide table row, with a 320px-wide video) doesn't fit inline there, so
  // opening it in a centered modal instead is what actually fixes both the
  // "keep it centered" and "big blank gap after the video" issues.
  function openGalleryModal(movie) {
    el.galleryModalContent.innerHTML = "";
    el.galleryModalContent.appendChild(buildDetail(movie));
    el.galleryModalOverlay.classList.remove("hidden");
  }

  function closeGalleryModal() {
    el.galleryModalOverlay.classList.add("hidden");
    // stop playback instead of leaving a paused video with sound state lingering
    const video = el.galleryModalContent.querySelector(".trailer-video");
    if (video) video.pause();
    el.galleryModalContent.innerHTML = "";
  }

  // ---- helpers ---------------------------------------------------------
  async function fetchJson(url) {
    const res = await fetch(url);
    if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
    return res.json();
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({
      "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"
    }[c]));
  }

  init();
})();