# roberts-ai-tools — working rules

## SharePoint: always prompt before creating or modifying

**Never create, upload, overwrite, move, rename, or delete anything in
SharePoint without asking first and receiving an explicit yes.**

This applies to every SharePoint location, every file type, and every tool
(`sharepoint_upload_file`, `sharepoint_update_file`, `sharepoint_create_folder`,
`sharepoint_copy_item`, `sharepoint_move_item`, `sharepoint_rename_item`,
`sharepoint_delete_item`), on every interaction in this project — including
when a previous request in the same conversation already approved a similar
write. Approval is per action, not per session.

### How to work instead

1. Build the artifact locally, in this repo or the scratchpad.
2. Show it, or send the file, so it can be reviewed.
3. State plainly where it would go: site, library, full folder path, filename.
4. Say whether anything existing would be overwritten.
5. Wait for an explicit yes. Silence, "looks good", or approval of the
   *content* is not approval to upload.

### Why

`GRP SaaS (Bravura Security)-Team > Documents > Team > Process Docs` is a
live, working library the SaaS team relies on. Its docs are in use and are
trusted during deploys and incidents. An unrequested file in that folder is
noise at best; an overwrite is damage.

### Reading is fine

Read-only SharePoint operations — search, folder listing, reading file
contents — need no prompt. Prefer them.

## Other notes

- Platform is built with Terraform and deployed with Ansible. Python is the
  primary language.
- Process doc drafts live in `process-docs/`. They are drafts until
  explicitly published; nothing in that directory is live documentation.
