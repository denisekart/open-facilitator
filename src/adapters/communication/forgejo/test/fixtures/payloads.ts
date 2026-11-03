// Recorded Forgejo 16 webhook payload shapes (subset — the fields the
// classifier reads), keyed per step-3 fixture law.
export const issueOpened = {
  action: "opened",
  number: 1,
  repository: { full_name: "demo/widgets" },
  sender: { login: "alice" },
  issue: { number: 1, body: "Summarize the repository layout", labels: [] },
};

export const issueLabeled = {
  action: "labeled",
  number: 1,
  repository: { full_name: "demo/widgets" },
  sender: { login: "alice" },
  label: { id: 7, name: "ofac:work" },
  issue: {
    number: 1,
    body: "Summarize the repository layout",
    labels: [{ id: 7, name: "ofac:work" }],
  },
};

export const issueLabeledOther = {
  action: "labeled",
  number: 1,
  repository: { full_name: "demo/widgets" },
  sender: { login: "alice" },
  label: { id: 3, name: "bug" },
  issue: { number: 1, labels: [{ id: 3, name: "bug" }] },
};

// Forgejo 16 renamed the labeling action: top-level label is null and the
// new label rides issue.labels[] (verified live against 16.0.7).
export const issueLabelUpdated = {
  action: "label_updated",
  number: 1,
  repository: { full_name: "demo/widgets" },
  sender: { login: "alice" },
  label: null,
  issue: {
    number: 1,
    body: "Summarize the repository layout",
    labels: [{ id: 7, name: "ofac:work" }],
  },
};

export const commentOnIssue = {
  action: "created",
  repository: { full_name: "demo/widgets" },
  sender: { login: "bob" },
  comment: { id: 10, body: "Please also cover the timeout path." },
  issue: { number: 1 },
};

export const commentOnPr = {
  action: "created",
  repository: { full_name: "demo/widgets" },
  sender: { login: "bob" },
  comment: { id: "11", body: "Nit: rename the retry counter." },
  issue: {
    number: 2,
    pull_request: {
      url: "http://git.localhost/api/v1/repos/demo/widgets/pulls/2",
      head: { ref: "feature/summarizer" },
    },
  },
};

export const prOpened = {
  action: "opened",
  number: 2,
  repository: { full_name: "demo/widgets" },
  sender: { login: "alice" },
  pull_request: {
    number: 2,
    body: "Adds the summarizer",
    merged: false,
    head: { sha: "abc123", ref: "feature/summarizer" },
  },
};

export const prClosedMerged = {
  action: "closed",
  number: 2,
  repository: { full_name: "demo/widgets" },
  sender: { login: "alice" },
  pull_request: { number: 2, merged: true, head: { sha: "abc123" } },
};

// Forgejo 16.x fires review submissions under per-action kinds carrying
// `review: {type, content}` (verified live against 16.0.7).
export const reviewSubmitted = {
  action: "reviewed",
  review: {
    id: 4,
    type: "pull_request_approved",
    content: "Approve with the retry clause reworded.",
  },
  repository: { full_name: "demo/widgets" },
  sender: { login: "reviewer" },
  pull_request: { number: 2, head: { sha: "abc123" } },
};

export const statusFailure = {
  sha: "abc123",
  status: "failure",
  description: "test suite red",
  context: "forgejo/default",
  repository: { full_name: "demo/widgets" },
  sender: { login: "ci-bot" },
};

export const botLabeled = {
  ...issueLabeled,
  sender: { login: "ofac-bot" },
};
