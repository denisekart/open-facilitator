// Vitest for the CDK package only; snapshots commit under test/__snapshots__.
export default {
  test: {
    include: ["test/**/*.test.ts"],
    testTimeout: 120_000,
  },
};
