/** One active destination refresh and one newest pending refresh per panel. */
export class PreparationReadCoordinator {
  private generation = 0;
  private running = false;
  private pending: ((isCurrent: () => boolean) => Promise<void>) | null = null;

  invalidate(read: (isCurrent: () => boolean) => Promise<void>): void {
    // Fence responses synchronously, before scheduling any asynchronous work.
    this.generation++;
    this.pending = read;
    this.schedule();
  }

  cancel(): void {
    this.generation++;
    this.pending = null;
  }

  private schedule(): void {
    if (this.running) return;
    this.running = true;
    queueMicrotask(() => { void this.drain(); });
  }

  private async drain(): Promise<void> {
    try {
      while (this.pending !== null) {
        const read = this.pending;
        this.pending = null;
        const generation = this.generation;
        await read(() => generation === this.generation);
      }
    } finally {
      this.running = false;
    }
  }
}
