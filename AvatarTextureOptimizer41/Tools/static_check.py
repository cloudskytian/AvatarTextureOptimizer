#!/usr/bin/env python3
"""Static sanity checks for ATO C# sources (brace balance, #if balance, duplicate types, JSON validity).
ATO C# 源码静态检查（括号平衡、#if 平衡、类型查重、JSON 有效性）。
Run from the package root:  python3 Tools/static_check.py"""
import json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # package root. 包根。
issues = []

def lex_check(src, path):
    bal = {"{": 0, "(": 0, "[": 0}
    i, n = 0, len(src)
    while i < n:
        c = src[i]; nxt = src[i + 1] if i + 1 < n else ""
        if c == '/' and nxt == '/':
            i = src.find("\n", i); i = n if i < 0 else i + 1; continue
        if c == '/' and nxt == '*':
            j = src.find("*/", i + 2)
            if j < 0: return [f"{path}: unterminated block comment"]
            i = j + 2; continue
        if c == '@' and nxt == '"':
            i += 2
            while i < n:
                if src[i] == '"' and src[i+1:i+2] == '"': i += 2; continue
                if src[i] == '"': break
                i += 1
            i += 1; continue
        if c == '"':
            i += 1
            while i < n and src[i] != '"':
                if src[i] == '\\': i += 2; continue
                i += 1
            i += 1; continue
        if c == "'":
            i += 1
            while i < n and src[i] != "'":
                if src[i] == '\\': i += 2; continue
                i += 1
            i += 1; continue
        if c == '{': bal["{"] += 1
        elif c == '}':
            bal["{"] -= 1
            if bal["{"] < 0: return [f"{path}: unbalanced '}}' at {i}"]
        elif c == '(': bal["("] += 1
        elif c == ')':
            bal["("] -= 1
            if bal["("] < 0: return [f"{path}: unbalanced ')' at {i}"]
        elif c == '[': bal["["] += 1
        elif c == ']':
            bal["["] -= 1
            if bal["["] < 0: return [f"{path}: unbalanced ']' at {i}"]
        i += 1
    return [f"{path}: unbalanced '{k}' ({v})" for k, v in bal.items() if v != 0]

declared = {}
for dirpath, _, files in os.walk(ROOT):
    if "Tools" in dirpath and "AtoCoreTests" in dirpath: continue
    for f in files:
        if f.endswith(".cs"):
            p = os.path.relpath(os.path.join(dirpath, f), ROOT)
            src = open(os.path.join(dirpath, f), encoding="utf-8", errors="replace").read()
            issues += lex_check(src, p)
            if src.count("#if") != src.count("#endif"):
                issues.append(f"{p}: unbalanced #if/#endif")
            for m in re.finditer(r"\b(?:class|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)", src):
                t = m.group(1)
                if t in declared and declared[t] != p:
                    issues.append(f"{p}: duplicate type '{t}' (also {declared[t]})")
                declared.setdefault(t, p)
        elif f.endswith(".json"):
            p = os.path.relpath(os.path.join(dirpath, f), ROOT)
            try:
                json.load(open(os.path.join(dirpath, f), encoding="utf-8"))
            except Exception as e:
                issues.append(f"{p}: invalid JSON ({e})")
        elif f.endswith(".shader"):
            p = os.path.relpath(os.path.join(dirpath, f), ROOT)
            s = open(os.path.join(dirpath, f), encoding="utf-8", errors="replace").read()
            if s.count("{") != s.count("}"):
                issues.append(f"{p}: unbalanced shader braces")

print(f"static check: {len(declared)} types declared, {len(issues)} issues")
for it in issues:
    print("  ", it)
sys.exit(1 if issues else 0)
