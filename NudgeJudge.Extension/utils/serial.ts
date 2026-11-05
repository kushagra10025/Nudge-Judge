/**
 * Runs async jobs one after another, in the order they were queued. For read-change-write steps on shared state
 * (extension storage, registered scripts) that two events can start at the same moment.
 */
export function serialQueue() {
  let tail: Promise<unknown> = Promise.resolve();
  return <T>(job: () => Promise<T>): Promise<T> => {
    const run = tail.then(job, job);
    // A failed job mustn't stop the ones after it.
    tail = run.catch(() => {});
    return run;
  };
}
