Let's build a new provider set for openfacilitator. We have all the workload types already implemented, so we don't need to deviate from standard implementations for the following. Make sure to utilize whatever core packages we created for consumers to build their own integrations. If these core packages don't have the proper surface, increase the surface to fit the requirements (a consumer should be able to write their own integration).

Make sure that the integrations have an opt in for the three different subdomains we discussed and can be independently turned on - ISSUES, VCS, MRs (I think these were the subdomains we discussed, but correct me if wrong).

The main goal is to have the facilitator (or harness) natively integrate with the provider (and work with a human as a coworker). Issue execution, issue comments and discussions, git operations, mr creation, commenting, reviewing, responding to comments and maintaining a dialogue should be in the forefront of the capabilities.


Let subagents work on these two providers seperately

GitHub - use official documentation, skills, or anything else that might be useful to implement a proper control surface,
GitLab - feel free to spin up a local instance that can be used for testing,

Any tests or verifications during development should completely simulate AI - there will be no AI available during ci/development/release

This is a very rough outline. Let's plan it together and refine it before making an actual plan for implementation. Ask as many questions as needed - do not assume anything - ask the question.

Prefer asking more questions than less - more rounds of questions are expected, not just a single round. Let's nail down the functionality before we implement it

Let's refine and build up a proper plan together.