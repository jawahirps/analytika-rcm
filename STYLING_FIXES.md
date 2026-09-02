# Styling Consistency — Implementation Guide
## Priority Fixes & Refactoring Tasks

**Status:** In Progress | **Priority:** CRITICAL → MEDIUM
**Estimated Effort:** 20-30 developer hours
**Impact:** Code maintainability, theme flexibility, accessibility

---

## PHASE 1: CRITICAL FIXES (1-2 days)

### 1.1 Remove All Inline `style="border-radius"` Attributes

**Impact:** Ensures consistent border radius across all themes
**Files affected:** 47+ instances across 10+ views

#### Pattern to Replace
```html
<!-- Before -->
<button style="border-radius:8px;">Button</button>

<!-- After -->
<button class="rounded-md">Button</button>
```

#### File-by-File Checklist
- [ ] `/Views/Portal/Sync.cshtml` — 8 instances
- [ ] `/Views/Portal/Reconciliation.cshtml` — 6 instances
- [ ] `/Views/Admin/Credentials.cshtml` — 5 instances
- [ ] `/Views/Admin/Database.cshtml` — 4 instances
- [ ] `/Views/Admin/Roles.cshtml` — 3 instances
- [ ] Other Admin views — 21+ instances

#### Command to Find Remaining Issues
```bash
grep -r 'style=".*border-radius' /Analytika/Views --include="*.cshtml"
```

---

### 1.2 Replace All Inline Color Styles

**Impact:** Enables theme switching without code changes
**Files affected:** 86+ instances across all views

#### Patterns to Replace

```html
<!-- Pattern 1: Inline hex color -->
<div style="color:#0D1B2A;">Text</div>
→ <div class="text-navy">Text</div>

<!-- Pattern 2: Inline rgba color -->
<div style="color:rgba(0,59,77,0.5);">Muted text</div>
→ <div class="text-muted">Muted text</div>

<!-- Pattern 3: Inline background color -->
<div style="background:#f7f9f9;">Panel</div>
→ <div class="bg-surface">Panel</div>

<!-- Pattern 4: Custom CSS variable (OK in most cases, but document it) -->
<div style="color:var(--amber-light);">Amber</div>
→ Document which utility to create if it doesn't exist
```

#### Color Mapping Guide
| Inline Style | Use This Class | CSS Variable |
|---|---|---|
| `color:#0D1B2A` | `.text-navy` | `var(--c-navy)` |
| `color:#16313c` | `.text-navy` | `var(--text)` |
| `color:var(--amber-light)` | (check if util exists) | Use as-is |
| `background:#f7f9f9` | `.bg-surface` | `var(--bg)` |
| `background:var(--emerald-light)` | `.bg-emerald-pale` | (check if exists) |

#### Command to Find Remaining Issues
```bash
grep -r 'style=".*\(color\|background\|fill\)' /Analytika/Views --include="*.cshtml" | head -50
```

---

### 1.3 Fix Dark Mode Hardcoded Colors

**Impact:** Enables dark mode theme switching
**Files affected:** CSS file with 50+ hardcoded values

#### Examples in CSS to Fix
```css
/* Before */
.sb-progress-detail { color: #EAF4FB; }
.xml-preview { color: #A7EBF2; }

/* After */
.sb-progress-detail { color: var(--dt-text); }
html[data-theme="dark"] .sb-progress-detail { color: var(--dt-text); }

.xml-preview { color: var(--dt-accent); }
html[data-theme="dark"] .xml-preview { color: var(--dt-accent); }
```

#### Search Patterns in site.css
```bash
# Find hardcoded colors in CSS
grep -n 'color: #[0-9A-F]' /Analytika/wwwroot/css/site.css
grep -n 'background: #[0-9A-F]' /Analytika/wwwroot/css/site.css
grep -n 'rgba(.*[0-9].*[0-9])' /Analytika/wwwroot/css/site.css
```

---

### 1.4 Create Missing Utility Classes

**Impact:** Eliminates need for inline styles in existing views
**Files affected:** `/wwwroot/css/site.css`

#### Already Added to site.css
- ✓ `.text-navy`, `.text-slate`, `.text-teal`, `.text-cyan-light`
- ✓ `.bg-sky-wash`, `.bg-navy`
- ✓ `.rounded-xs`, `.rounded-sm`, `.rounded-md`, `.rounded-lg`, `.rounded-xl`, `.rounded-2xl`

#### Additional Utilities to Consider
```css
/* Button size standardization */
.btn-xs    { font-size: 0.7rem; padding: 0.25rem 0.5rem; }
.btn-md    { font-size: 0.875rem; padding: 0.5rem 1rem; }  /* Default */
.btn-lg    { font-size: 1rem; padding: 0.75rem 1.5rem; }

/* Additional gap utilities */
.gap-2-5 { gap: 0.625rem; }
.gap-3-5 { gap: 0.875rem; }

/* Additional padding utilities (already in Bootstrap but document) */
.px-2-5  { padding-left: 0.625rem; padding-right: 0.625rem; }
.py-1-5  { padding-top: 0.375rem; padding-bottom: 0.375rem; }
```

**Status:** Most are already in Bootstrap or site.css. Just document which to use.

---

## PHASE 2: HIGH PRIORITY FIXES (2-3 days)

### 2.1 Consolidate Duplicate Color Token Definitions

**Current State:**
- `.text-navy` was defined twice in site.css (fixed in Phase 1)
- Color aliases are 4 levels deep in some cases
- Different color mappings per skin

**Actions:**
1. Document the color token hierarchy
2. Create a color token reference table
3. Ensure each semantic color has ONE definition
4. Create an alias map document

**Deliverable:**
```markdown
# Color Token Reference

## Base Colors (defined in :root)
--deep-navy: #003B4D        → Use: var(--deep-navy) (or alias)
--classic-teal: #006884     → Use: var(--classic-teal)
...

## Semantic Aliases (use these)
--navy:      var(--deep-navy)     → Use in CSS
--emerald:   var(--classic-teal)  → Use in CSS
...

## CSS Utilities (use these in HTML)
.text-navy      ↦ color: var(--c-navy)       ↦ displayed as --navy color
.text-emerald   ↦ color: var(--emerald)
...
```

---

### 2.2 Standardize Button Styling

**Current Issues:**
- Padding varies: `.px-3.py-2`, `.px-4`, inline values
- Border-radius inconsistent
- Font weight varies: 500, 600, 700

**Solution:**
```html
<!-- Standard sizes (define once in CSS) -->
<button class="btn btn-sm">Small (px-3 py-2)</button>
<button class="btn">Default (px-4 py-3)</button>
<button class="btn btn-lg">Large (px-5 py-4)</button>

<!-- Always use semantic variants -->
<button class="btn btn-primary">Primary action</button>
<button class="btn btn-outline-emerald">Secondary action</button>
<button class="btn btn-ghost">Tertiary action</button>
```

**CSS Changes Needed:**
```css
.btn {
  font-weight: 500 !important;      /* Consistent weight */
  border-radius: var(--radius) !important;  /* Use token, not inline */
  padding: 0.5rem 1rem !important;  /* Default size */
  transition: all var(--transition);
}

.btn-sm {
  padding: 0.375rem 0.75rem !important;
}

.btn-lg {
  padding: 0.75rem 1.5rem !important;
}
```

---

### 2.3 Unify Form Control Styling

**Current Issues:**
- Label sizing inconsistent: `.small .fw-semibold`, `.form-label`, etc.
- Input focus states vary per skin (blur: 18px, 22px, 2px)
- Select2 uses hardcoded colors

**Solution:**
```html
<!-- Always use this structure -->
<div class="mb-3">
  <label class="form-label fw-semibold">Label Text</label>
  <input type="text" class="form-control" placeholder="Placeholder" />
  <small class="form-text text-muted">Helper text</small>
</div>

<!-- For small/compact forms -->
<div class="mb-2">
  <label class="form-label fw-semibold fs-75">Small Label</label>
  <input type="text" class="form-control form-control-sm" />
</div>
```

**CSS to Standardize:**
```css
/* Form labels */
.form-label {
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--text-secondary);
  text-transform: uppercase;
  letter-spacing: 0.25px;
}

/* Focus states (consistent across all skins) */
.form-control:focus,
.form-select:focus {
  border-color: var(--emerald);
  box-shadow: 0 0 0 4px rgba(0,139,139,0.18);
  outline: none;
}

/* Remove skin-specific blur overrides */
```

---

### 2.4 Accessibility Audit & Fixes

**Issues Found:**
1. Contrast ratio on `--dt-text-3` (4.2:1) ✗ WCAG AAA
2. Form labels too small (0.8rem / 12.8px) — acceptable but better at 14px
3. Table headers too small (0.75rem / 12px) — minimum, not ideal

**Fixes:**
```css
/* Increase form label size */
.form-label { font-size: 0.875rem; }  /* 0.8rem → 0.875rem (14px) */

/* Increase table header minimum */
.table thead th { font-size: 0.85rem; }  /* 0.75rem → 0.85rem */

/* Adjust dark mode text hierarchy */
--dt-text-3: rgba(234,244,251,0.68);  /* 0.54 → 0.68 for 4.8:1 contrast */
```

**Testing Required:**
- [ ] Contrast check all text colors vs backgrounds
- [ ] Test with screen reader (NVDA, JAWS, VoiceOver)
- [ ] Keyboard navigation (Tab, Enter, Space)
- [ ] Focus visibility on all interactive elements

---

## PHASE 3: MEDIUM PRIORITY (Week 2)

### 3.1 Component Library Documentation

**Deliverable:** Document every component pattern used in the app

```markdown
# Button Component

## Variants
- `.btn.btn-primary` — Primary action (strong CTA)
- `.btn.btn-outline-emerald` — Secondary action
- `.btn.btn-ghost` — Tertiary action
- `.btn.btn-danger` — Destructive action

## Sizes
- `.btn-sm` — Small buttons (side actions, tables)
- `.btn` — Default (most uses)
- `.btn-lg` — Large (prominent CTAs)

## States
- `:hover` — Automatic
- `:active` — Automatic
- `:disabled` — Add `disabled` attribute
- `:focus` — Automatic focus ring

## Accessibility
- Button text must be clear and descriptive
- Icon-only buttons need `aria-label`
- Use `<button>` or `<a>`, not `<div>`

## Examples
[Show 3-5 real examples from the app]
```

---

### 3.2 Theme System Refinement

**Tasks:**
1. Ensure all 5 skins define consistent spacing values
2. Audit color differences per skin
3. Create skin comparison matrix
4. Document when to use each skin

**Deliverable:**
| Aspect | Classic | Obsidian | Ledger | Aurora | Fable |
|--------|---------|----------|--------|--------|-------|
| Font Display | Inter | Sora | Space Grotesk | Sora | Fraunces |
| Radius-SM | 6px | 8px | 4px | 10px | 10px |
| Primary Color | Teal | Teal | Teal | Purple | Gold |
| Style | Clean | Glass | Editorial | Gradient | Luxe |

---

### 3.3 View-by-View Refactoring

**High-priority views** (most inline styles):

1. `/Views/Portal/Sync.cshtml` — 20+ issues
2. `/Views/Portal/Reconciliation.cshtml` — 15+ issues
3. `/Views/Admin/Credentials.cshtml` — 12+ issues
4. `/Views/Admin/Database.cshtml` — 8+ issues

**For each file:**
- [ ] Remove all inline `border-radius` styles
- [ ] Replace inline `color` with utility classes
- [ ] Replace inline `background` with utility classes
- [ ] Replace inline `padding`/`margin` with Bootstrap utilities
- [ ] Test in light + dark modes
- [ ] Validate HTML structure

---

## PHASE 4: LOW PRIORITY (Nice-to-Have)

### 4.1 CSS Minification & Optimization

```bash
# Current: site.css = 6000+ lines, 200+ KB
# After minification: ~80 KB

# Remove unused classes
# Consolidate similar rules
# Document high-level structure
```

### 4.2 Performance Optimization

- Extract font loading to preload
- Defer non-critical CSS
- Use CSS Grid instead of Flexbox where appropriate
- Profile CSS performance with DevTools

### 4.3 CSS-in-JS Consideration

For future: Consider Tailwind CSS or CSS modules to eliminate this issue entirely.

---

## VALIDATION CHECKLIST

### Before Marking Complete
- [ ] No inline `style="color:`
- [ ] No inline `style="border-radius:`
- [ ] No inline `style="background:`
- [ ] All buttons use semantic classes (btn-primary, btn-outline-*, etc.)
- [ ] All form labels use `.form-label`
- [ ] All cards use `.card` class
- [ ] All text colors use utility classes or CSS variables
- [ ] All border radius uses utility classes
- [ ] All spacing uses Bootstrap utilities

### Testing Before Deploy
- [ ] Light mode + Classic skin
- [ ] Dark mode + Classic skin
- [ ] Light mode + Obsidian skin
- [ ] Light mode + Ledger skin
- [ ] WCAG contrast audit
- [ ] Mobile responsive (320px, 768px, 1200px)
- [ ] Keyboard navigation (Tab only)
- [ ] Focus indicators visible

---

## QUICK FIX COMMANDS

### Find and List All Inline Styles
```bash
# Find color styles
grep -r 'style=".*color' /Analytika/Views --include="*.cshtml" > color_styles.txt

# Find border-radius styles
grep -r 'style=".*border-radius' /Analytika/Views --include="*.cshtml" > radius_styles.txt

# Find all inline styles
grep -r 'style=' /Analytika/Views --include="*.cshtml" > all_inline_styles.txt
```

### Replace Pattern Examples
```bash
# Dry run: Show what would change
sed -n 's/style="border-radius:8px;"/class="rounded-md"/p' file.cshtml

# Actually replace (be careful!)
sed -i 's/style="border-radius:8px;"/class="rounded-md"/g' file.cshtml
```

---

## ESTIMATED TIMELINE

| Phase | Tasks | Hours | Status |
|-------|-------|-------|--------|
| Phase 1 (Critical) | Remove inline styles, add utilities | 8-10 | In Progress |
| Phase 2 (High) | Consolidate tokens, standardize components | 10-12 | Planned |
| Phase 3 (Medium) | Documentation, refactor views | 8-10 | Planned |
| Phase 4 (Low) | Optimization, future planning | 4-6 | Future |
| **TOTAL** | | **30-38 hours** | |

---

## DEPENDENCIES

1. No dependencies — can be done incrementally
2. Recommend completing Phase 1 before adding new features
3. Phase 2 should be completed before next major skin addition

---

## ROLLBACK PLAN

If issues arise:
1. Git revert to last working commit
2. Run SCSS compilation to regenerate CSS
3. Re-validate all themes

---

## SUCCESS CRITERIA

After completing all phases:

✓ Zero inline `style=` attributes with color, border-radius, or padding
✓ All color changes can be made by updating CSS variables only
✓ Dark mode can be toggled without code changes
✓ New skins can be added without touching view files
✓ WCAG 2.1 AA compliance on all text
✓ Accessibility audit passes
✓ Code review: Consistent style usage across all views

---

**Last Updated:** September 2, 2026
**Owner:** Design System Team
**Next Review:** After Phase 1 completion
