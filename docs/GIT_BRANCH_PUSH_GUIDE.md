# Git branch create, commit & push guide

Use this when working on the **v6api** repository.  
Do **not** push directly to `main`. Create your own branch, push there, and your manager will merge into `main`.

**Repo:** https://github.com/tech-ezofis-git/v6api

---

## 1. Clone (first time only)

```powershell
cd D:\
git clone https://github.com/tech-ezofis-git/v6api.git
cd v6api
```

---

## 2. Create your branch (from latest `main`)

Replace `dev-yourname` with your branch name (example: `dev-aravinth`).

```powershell
cd D:\v6api

git checkout main
git pull origin main

git checkout -b dev-yourname
git push -u origin dev-yourname
```

| Flag | Meaning |
|------|---------|
| `-b` | Create a new branch and switch to it |
| `-u` | Link local branch to remote (first push only) |

---

## 3. Next day / next session (branch already exists)

```powershell
cd D:\v6api

git checkout dev-yourname
git pull origin dev-yourname
```

---

## 4. Make changes → commit → push

```powershell
# See what changed
git status
git diff

# Stage files (all changes, or specific files)
git add .
# OR
git add path\to\file.cs

# Commit
git commit -m "Short clear description of why you changed it."

# Push to YOUR branch (not main)
git push origin dev-yourname
```

---

## 5. Useful checks

```powershell
# Current branch name
git branch --show-current

# Local + remote branches
git branch -a

# Recent commits
git log -5 --oneline
```

---

## 6. Manager: merge your branch into `main`

### Option A — GitHub Pull Request (preferred)

1. Open: https://github.com/tech-ezofis-git/v6api  
2. Create Pull Request: **`dev-yourname` → `main`**  
3. Review and merge  

### Option B — Command line

```powershell
git checkout main
git pull origin main
git merge dev-yourname
git push origin main
```

---

## 7. Sync your branch after `main` was updated

If someone else merged to `main` and you need those changes:

```powershell
git checkout dev-yourname
git pull origin main
# resolve conflicts if any, then:
git push origin dev-yourname
```

---

## Do / Don't

| Do | Don't |
|----|--------|
| Work on `dev-yourname` | Push to `main` yourself |
| `git pull` before starting work | Force push (`git push --force`) unless asked |
| Write a clear commit message | Commit secrets (`appsettings.json`, passwords, keys) |
| Push to `origin/dev-yourname` | Commit `.env` or local credentials |

---

## Example: developer `aravinth`

```powershell
cd D:\v6api

git checkout main
git pull origin main

git checkout -b dev-aravinth
git push -u origin dev-aravinth

# later, after code changes:
git add .
git commit -m "Fix workflow JSON blob storage fallback."
git push origin dev-aravinth
```

Then manager merges **`dev-aravinth` → `main`**.

---

## Quick copy-paste (existing branch)

```powershell
cd D:\v6api
git checkout dev-yourname
git pull origin dev-yourname
git add .
git commit -m "Your message here."
git push origin dev-yourname
```
