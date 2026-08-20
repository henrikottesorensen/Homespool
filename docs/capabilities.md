# Capabilities: what an account may do, and how to narrow a token

You do not need this document to use Homespool on your own. A single-user install grants you
everything, and nothing here has to be configured. Read it if you are **sharing a printer with
somebody else**, **minting an API token for a script**, or working out **why a script that used to
work now gets a 403**.

## The vocabulary

Eleven capabilities. Every permission decision in Homespool is one of them.

| capability | what it permits |
|---|---|
| `ViewPrinter` | See that a printer exists at all, and its name, state and telemetry |
| `ViewQueue` | See what a printer is going to print, and why the queue is waiting |
| `ViewHistory` | See what a printer has printed |
| `Print` | Queue a print — and cancel your own queue entry or stop your own running print |
| `ControlPrinter` | Stop, pause, resume, ready, idle and preheat — on **anybody's** print — and reorder or cancel anybody's queue entry |
| `ManagePrinter` | Rename a printer, change its location, re-enrol it, and allow it to be readied remotely |
| `ViewCamera` | See a camera and its picture |
| `ManageCamera` | Add, change and remove cameras |
| `ViewOwnFiles` | List and download **your own** files |
| `UploadOwnFiles` | Upload a file under a name you are not already using |
| `ManipulateOwnFiles` | Rename, delete and overwrite **your own** files |

**Without `ViewPrinter` a printer is not read-only, it is invisible.** It does not appear in listings,
and asking for it by name answers *no such printer* — the same answer somebody who is not on its team
gets, so a 404 never confirms that a printer exists.

### Some capabilities imply others

An act on a printer or camera implies being able to see it. You cannot print on a printer you cannot
see, and you cannot tell whether a camera is configured correctly without looking at its picture.

| ticking this | also grants |
|---|---|
| `Print`, `ControlPrinter` or `ManagePrinter` | `ViewPrinter` |
| `ManageCamera` | `ViewCamera` |

The file capabilities imply nothing: a file is addressed by the name you already know, so deleting one
does not require being able to list them.

## Teams: what a person may do

Printers and cameras belong to a **team**, not to a person. What you may do to a printer is what your
membership of its team grants.

**Teams govern printers and cameras only.** The three file capabilities are not part of a membership —
your files are yours, and no team grants or withholds them. They exist to narrow a token, and that is
all they do.

Every account gets its own team when it is created, and its creator holds every printer and camera
capability on it. When somebody accepts an invitation into an existing team, they get everything
except `ManagePrinter` and `ManageCamera` — they can print and run the machine, but not rename or
re-enrol it.

> **There is no screen for changing a membership yet.** Those two are currently the only
> combinations Homespool creates. If you need a member who can only watch, that is not something the
> interface can express today.

## Tokens: what a credential may do

A personal access token is created under **Account → API tokens**, and you choose what it may do when
you create it. **Nothing is ticked to begin with** — tick what the script needs, and nothing else. A
token you create without thinking about this can do nothing at all, which is the intended direction to
fail in; **Tick all** is there for the rare key that genuinely wants everything.

**A token with no capabilities is refused.** You will be asked to choose at least one rather than
handed a credential that fails on its first call.

**A token can never do more than you can.** What it may do is what your memberships allow *and* what
its scope names — narrowing only. Ticking `ManagePrinter` on a token does not let it manage a printer
you may only watch.

Two consequences worth knowing:

- **A token is not affected by capabilities you gain later.** Its scope lists what was ticked when it
  was made. If a capability is added to Homespool in a future version, existing tokens do not silently
  acquire it.
- **A token cannot manage your account.** It cannot change your password, create another token, manage
  invitations or download a printer's provisioning bundle. Those need a signed-in browser session.

### A scope for a slicer

PrusaSlicer's print-host integration needs to upload a file and print it, and nothing else. Tick:

- `UploadOwnFiles`
- `Print`

That key can then send a model and start it, and **cannot delete anything** — which matters, because
it lives in a slicer's configuration file on a laptop.

### Revoking

Revoking a token is deleting it, and it stops working immediately. **Changing your password revokes
every token you have**, on the assumption that somebody changing their password believes an account is
compromised.

One thing revocation does not do: **a print already queued by a token goes on printing.** The queue
records what a job was accepted under and keeps running it. Removing the person's access to the
printer does stop it.

## Who may stop a print

| | your own print | somebody else's |
|---|---|---|
| `Print` | yes | no |
| `ControlPrinter` | yes | yes |

The same split applies to cancelling a queue entry. Reordering the queue is `ControlPrinter` either
way, because one queue is shared and moving your entry moves everybody's.

This is the arrangement a print room has: you can withdraw your own work, and running the machine for
everybody is a separate job.

## Files are always your own

Homespool stores your uploads under your own account. **Nobody else can read, rename or delete them**,
whatever their capabilities and whatever team they are on — there is no permission that grants access
to another person's files, because there is nothing for it to grant.

The three file capabilities exist to narrow a *token*, not to share anything: they answer "may this
key touch my files", never "may this person touch mine".

## When a request is refused

| status | meaning |
|---|---|
| **401** | No credential, or one that is not recognised. Check the `Authorization` header. |
| **403** | Recognised, but not permitted. Either your membership does not allow it, or the token's scope does not name it. |
| **404** on a printer | Either it does not exist or you cannot see it. Homespool does not tell the two apart on purpose. |

**A 403 caused by a token's scope names the capability that was missing**, so the fix is to mint a
replacement with that box ticked. The server logs the same line, which answers *why did my script stop
working* from the host side.

A 403 with no capability named is the other kind: your **team** does not permit it, and no token will
help — somebody with `ManagePrinter` on that team has to grant it.
