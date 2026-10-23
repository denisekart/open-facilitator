---
name: no-push-to-origin
description: "Never push to a remote — end at a local commit and let the user push"
condition: ["(?i)\\bgit\\b[^\"]*push", "(?i)\\bpush(ing|ed)?\\s+(to\\s+)?(origin|main|the\\s+remote|a\\s+branch|branch)", "--set-upstream"]
scope: ["tool:bash", "tool:todo", "text"]
---

Never push to a remote. Finish at a local commit on the working branch and tell the user it is ready for them to push. Do not plan, todo-list, or execute push/PR/merge steps as your own actions — pushing is the user's responsibility.