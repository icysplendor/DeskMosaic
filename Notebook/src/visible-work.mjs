// Keep the entire editable document. Defer only display work, never Markdown.
export class VisibleWork {
  constructor() {
    this.jobs = new Map();
    this.ready = new Map();
    this.timer = null;
    this.observer =
      typeof IntersectionObserver === "function"
        ? new IntersectionObserver(
            (entries) => {
              for (const entry of entries) {
                if (!entry.isIntersecting) continue;
                const job = this.jobs.get(entry.target);
                this.jobs.delete(entry.target);
                this.observer.unobserve(entry.target);
                if (job) this.enqueue(entry.target, job);
              }
            },
            { rootMargin: "300px 0px" },
          )
        : null;
  }
  schedule(element, job) {
    this.cancel(element);
    if (this.observer) {
      this.jobs.set(element, job);
      this.observer.observe(element);
    } else this.enqueue(element, job);
  }
  enqueue(element, job) {
    this.ready.set(element, job);
    if (this.timer !== null) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      const next = this.ready.entries().next().value;
      if (!next) return;
      const [target, work] = next;
      this.ready.delete(target);
      if (target.isConnected) work();
      // One block per task lets input and painting run between highlights.
      if (this.ready.size) {
        const [target, work] = this.ready.entries().next().value;
        this.enqueue(target, work);
      }
    }, 0);
  }
  cancel(element) {
    this.observer?.unobserve(element);
    this.jobs.delete(element);
    this.ready.delete(element);
  }
  prune() {
    for (const element of this.jobs.keys())
      if (!element.isConnected) this.cancel(element);
    for (const element of this.ready.keys())
      if (!element.isConnected) this.cancel(element);
  }
  reset() {
    this.observer?.disconnect();
    this.jobs.clear();
    this.ready.clear();
    clearTimeout(this.timer);
    this.timer = null;
  }
}
