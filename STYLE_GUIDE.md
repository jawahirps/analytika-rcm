# Analytika RCM — STYLE GUIDE
## Ghaf Business Intelligence Design System v6

**Last Updated:** September 2, 2026
**Status:** Active — In Implementation
**Audience:** Developers, UI Engineers, Designers

---

## TABLE OF CONTENTS
1. [Design Tokens](#design-tokens)
2. [Color System](#color-system)
3. [Typography](#typography)
4. [Spacing & Layout](#spacing--layout)
5. [Components](#components)
6. [Accessibility](#accessibility)
7. [Dark Mode & Themes](#dark-mode--themes)
8. [Common Patterns](#common-patterns)
9. [What NOT To Do](#what-not-to-do)

---

## DESIGN TOKENS

### CSS Custom Properties (Variables)
All styling should use CSS custom properties defined in `/wwwroot/css/site.css`. Never use hardcoded values in HTML.

#### Base Color Tokens
```css
--deep-navy:    #003B4D (primary dark)
--classic-teal: #006884 (primary accent)
--forest-teal:  #008B8B (secondary accent)
--seafoam-blue: #005a6f (tertiary)
--sage-teal:    #66B2B2 (light accent)
--ice-blue:     #002d3d (darker navy)
--soft-white:   #eaf6f8 (off-white)
--gold:         #B88924 (warning color)
--coral:        #B94A3F (danger color)
--mint:         #2F8F72 (success color)
```

#### Semantic Aliases (Use These)
```css
--navy:         var(--deep-navy)        /* Primary text color */
--ink:          var(--deep-navy)        /* Alternative to navy */
--emerald:      var(--classic-teal)     /* Primary accent */
--emerald-dark: var(--forest-teal)      /* Darker accent */
--teal-vivid:   var(--classic-teal)     /* Vivid accent */
--success:      var(--mint)             /* Success color */
--warning:      var(--gold)             /* Warning color */
--danger:       var(--coral)            /* Danger color */
--info:         var(--forest-teal)      /* Info color */
```

#### Surface & Layout Tokens
```css
--bg:           #f7f9f9          /* Page background */
--surface:      rgba(255,255,255,0.88)     /* Card background */
--surface-strong: rgba(255,255,255,0.95)   /* Modal background */
--surface-alt:  rgba(0,59,77,0.06)         /* Alternate surface */
--glass:        rgba(255,255,255,0.74)     /* Glassmorphism */
--glass-strong: rgba(255,255,255,0.94)     /* Stronger glass */
```

#### Border & Divider Tokens
```css
--border:       rgba(198,226,233,0.85)    /* Standard border */
--border-light: rgba(198,226,233,0.60)    /* Light border */
--glass-border: rgba(0,59,77,0.12)        /* Glass effect border */
```

#### Spacing Tokens
```css
--radius-sm:  6px    /* Small rounded corners (buttons, chips) */
--radius:     8px    /* Default rounded corners (inputs, cards) */
--radius-lg:  10px   /* Large rounded corners (card headers) */
--radius-xl:  12px   /* Extra large (modals) */
```

#### Shadow Tokens
```css
--shadow-xs:  0 1px 2px rgba(0,59,77,0.04)   /* Minimal */
--shadow-sm:  0 8px 22px rgba(0,59,77,0.08)  /* Small */
--shadow-md:  0 14px 36px rgba(0,59,77,0.11) /* Medium */
--shadow-lg:  0 22px 58px rgba(0,59,77,0.16) /* Large */
```

#### Effects
```css
--blur:         blur(18px) saturate(170%)    /* Glass morphism blur */
--transition:   0.18s ease                   /* Standard animation */
```

---

## COLOR SYSTEM

### Using Colors in HTML/Razor

#### Text Colors
```html
<!-- Primary text (default on white) -->
<p class="text-navy">Primary text</p>

<!-- Secondary/Muted text -->
<p class="text-muted">Secondary text</p>

<!-- Semantic colors -->
<p class="text-success">Success message</p>
<p class="text-warning">Warning message</p>
<p class="text-danger">Error message</p>

<!-- Accent colors -->
<p class="text-emerald">Accent text</p>
<p class="text-cyan-light">Cyan accent</p>
<p class="text-slate">Slate/secondary</p>
```

#### Background Colors
```html
<!-- Surface backgrounds (use these, not inline styles) -->
<div class="bg-surface">Card or panel</div>
<div class="bg-surface-alt">Alternative surface</div>
<div class="bg-sky-wash">Light blue wash</div>

<!-- Semantic backgrounds (use Bootstrap classes) -->
<div class="bg-success-subtle">Success background</div>
<div class="bg-warning-subtle">Warning background</div>
<div class="bg-danger-subtle">Danger background</div>
```

### Color Usage Rules

**DO:**
- Use CSS variables for all colors: `color: var(--emerald)`
- Use utility classes: `class="text-navy bg-surface"`
- Use semantic classes for alerts/badges: `class="alert alert-success"`

**DON'T:**
- Use inline `style="color: #003B4D"` — WRONG!
- Use hardcoded hex colors in CSS
- Use Bootstrap defaults like `text-dark` or `bg-light`
- Mix hex values with CSS variables

### Contrast & Accessibility
All text colors must meet WCAG AA minimum contrast (4.5:1 for normal text):
- ✓ Navy (`#16313c`) on white = 7.2:1
- ✓ Teal (`#006884`) on white = 6.9:1
- ✓ Muted (`#36566a`) on white = 6.8:1
- ✗ Muted on sage-teal background = 2.1:1 (INVALID)

Always test color combinations in both light and dark modes.

---

## TYPOGRAPHY

### Font Stack
```css
body { font-family: 'Inter', 'Segoe UI', system-ui, -apple-system, sans-serif; }
```

### Font Sizes

#### Body Text
```html
<p>Regular paragraph (14px / 0.875rem)</p>
<p class="small">Small text (12.8px / 0.8rem)</p>
<p class="fs-75">Extra small (12px / 0.75rem)</p>
```

#### Headings
```html
<h1>36px - Page title (use .page-title class instead)</h1>
<h2>28px - Section headers</h2>
<h3>24px - Sub-section headers</h3>
<h4 class="page-title">20px - Page title (RECOMMENDED)</h4>
<h5>16px - Card headers</h5>
<h6>14px - Subsection headers</h6>
```

#### Utility Font Sizes
```html
<p class="fs-65">10.4px</p>
<p class="fs-70">11.2px</p>
<p class="fs-75">12px    <!-- Minimum recommended for UI labels --></p>
<p class="fs-80">12.8px  <!-- Good for form labels --></p>
<p class="fs-85">13.6px</p>
<p class="fs-90">14.4px  <!-- Good for body text --></p>
```

### Font Weights
```html
<p class="fw-normal">400 - Regular</p>
<p class="fw-medium">500 - Medium</p>
<p class="fw-semibold">600 - Semibold</p>
<p class="fw-bold">700 - Bold</p>
```

### Text Styles
```html
<!-- Labels (use for form labels, stat labels) -->
<label class="form-label">Form Label</label>

<!-- Page headers -->
<h4 class="page-title">Page Title</h4>
<small class="page-subtitle">Subtitle or description</small>

<!-- Muted/Secondary text -->
<small class="text-muted">Muted or secondary text</small>
<span class="text-body-secondary">Alternative muted class</span>
```

### Line Height
```css
body { line-height: 1.6; }      /* Body text */
h1, h2, h3, h4, h5, h6 { line-height: 1.3; }  /* Headings */
```

---

## SPACING & LAYOUT

### Spacing Scale
Spacing uses Bootstrap's spacing utilities (multiples of 0.25rem):

```html
<!-- Margin (m-*) -->
<div class="m-0">0rem</div>
<div class="m-1">0.25rem</div>
<div class="m-2">0.5rem</div>
<div class="m-3">0.75rem</div>
<div class="m-4">1rem</div>
<div class="m-5">1.5rem</div>

<!-- Padding (p-*) -->
<div class="p-2">0.5rem</div>
<div class="p-3">0.75rem</div>
<div class="p-4">1rem</div>

<!-- Gap (g-*) - for flex/grid -->
<div class="d-flex gap-2">Item 1</div>
<div class="d-flex gap-3">Item 2</div>
<div class="d-flex gap-4">Item 3</div>
```

### Container Padding
```html
<!-- Use these specific classes for page padding -->
<div class="container-fluid py-4 px-4">
  <!-- content: 1.75rem top/bottom, 1rem left/right -->
</div>
```

### Border Radius
```html
<!-- Use token-based border radius classes (NOT inline styles!) -->
<div class="rounded-xs">4px</div>
<div class="rounded-sm">6px (small)</div>
<div class="rounded-md">8px (medium) — DEFAULT FOR INPUTS</div>
<div class="rounded-lg">10px (large)</div>
<div class="rounded-xl">12px (extra large) — DEFAULT FOR CARDS</div>
<div class="rounded-2xl">24px (2xl) — MODALS</div>
```

### Shadows
```html
<!-- Use shadow classes, not inline styles! -->
<div class="shadow-sm">Small shadow (cards, dropdowns)</div>
<div class="shadow-md">Medium shadow (modals, elevated)</div>
<div class="shadow-lg">Large shadow (floating panels)</div>
```

---

## COMPONENTS

### Buttons

#### Sizes
```html
<!-- Small buttons (secondary actions) -->
<button class="btn btn-sm px-3 py-2">Small Button</button>

<!-- Default buttons (primary actions) -->
<button class="btn px-4 py-2">Default Button</button>

<!-- Large buttons (prominent actions) -->
<button class="btn btn-lg px-5 py-3">Large Button</button>
```

#### Variants
```html
<!-- Primary (use for main actions) -->
<button class="btn btn-primary">Submit</button>

<!-- Outline variants (secondary actions) -->
<button class="btn btn-outline-emerald">Emerald Outline</button>
<button class="btn btn-outline-success">Export</button>
<button class="btn btn-outline-info">Info</button>
<button class="btn btn-outline-warning">Warning</button>
<button class="btn btn-outline-danger">Delete</button>

<!-- Ghost/Transparent (tertiary actions) -->
<button class="btn btn-ghost">Cancel</button>

<!-- Semantic buttons -->
<button class="btn btn-primary">Submit</button>
<button class="btn btn-danger">Delete</button>
```

#### Button Icon Spacing
```html
<!-- Icons before text -->
<button class="btn btn-primary">
  <i class="fas fa-save me-2"></i> Save
</button>

<!-- Icons after text -->
<button class="btn btn-outline-emerald">
  Download <i class="fas fa-download ms-2"></i>
</button>
```

#### Button Best Practices
- Use `.rounded-md` or `.rounded-lg` for border radius (NOT inline styles!)
- Use consistent padding: `.px-3.py-2` for small, `.px-4.py-3` for default
- Always use semantic variants (primary, danger, success) for action meaning
- Use `btn-outline-*` for secondary actions
- Put icons in `<i>` tags with appropriate spacing (me-2 / ms-2)

### Forms

#### Form Groups
```html
<div class="mb-3">
  <label class="form-label">Full Name</label>
  <input type="text" class="form-control" placeholder="Enter name" />
</div>
```

#### Input Sizing
```html
<!-- Standard inputs -->
<input type="text" class="form-control" />
<select class="form-select"></select>

<!-- Small inputs (use only in compact layouts) -->
<input type="text" class="form-control form-control-sm" />
<select class="form-select form-select-sm"></select>
```

#### Form Label Standards
```html
<!-- Required label format -->
<label class="form-label fw-semibold">Email Address</label>

<!-- With optional indicator -->
<label class="form-label fw-semibold">Phone <span class="text-muted">(optional)</span></label>

<!-- Font size: 0.8rem, weight: 600, color: text-secondary -->
```

#### Form Validation
```html
<!-- Success state -->
<input type="text" class="form-control is-valid" />

<!-- Error state -->
<input type="text" class="form-control is-invalid" />
<div class="invalid-feedback">This field is required.</div>
```

### Cards

#### Standard Card
```html
<div class="card">
  <div class="card-body">
    <h5 class="card-title">Card Title</h5>
    <p class="card-text">Card content goes here.</p>
  </div>
</div>
```

#### Card with Header
```html
<div class="card">
  <div class="card-header">
    <h5 class="mb-0">Header Title</h5>
  </div>
  <div class="card-body">
    Content here
  </div>
</div>
```

#### Stat Card (KPI)
```html
<div class="stat-card">
  <div class="d-flex align-items-center gap-3">
    <div class="stat-icon" style="background: var(--emerald-light);">
      <i class="fas fa-chart-line text-emerald"></i>
    </div>
    <div>
      <div class="stat-value">1,234</div>
      <div class="stat-label">Total Revenue</div>
    </div>
  </div>
</div>
```

#### Card Best Practices
- Use `.card` with default styling (already has borders, shadows, radius)
- Use `.card-body` for padding (not inline padding!)
- Use `.card-header` for header sections
- Use utility classes for spacing, not inline styles
- Don't override `border-radius` inline — use `.rounded-*` classes

### Tables

#### Standard Table
```html
<table class="table table-hover">
  <thead>
    <tr>
      <th>Column 1</th>
      <th>Column 2</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td>Data 1</td>
      <td>Data 2</td>
    </tr>
  </tbody>
</table>
```

#### Table Best Practices
- Font size: `0.825rem` (defined in CSS)
- Header background: `rgba(216,232,243,0.74)` (defined in CSS)
- Use `.table-hover` for interactive tables
- Don't override font sizes or colors inline
- Use `.table-responsive` for mobile scrolling

### Badges & Status Indicators

#### Badges (for tags/labels)
```html
<span class="badge badge-emerald">Emerald Badge</span>
<span class="badge badge-success">Success</span>
<span class="badge badge-warning">Warning</span>
<span class="badge badge-danger">Danger</span>
```

#### Status Pills (for status display)
```html
<span class="status-pill active">Active</span>
<span class="status-pill inactive">Inactive</span>
<span class="status-pill success">Completed</span>
<span class="status-pill failed">Failed</span>
```

#### Status Dots (for inline indicators)
```html
<span class="status-dot active"></span> Active
<span class="status-dot inactive"></span> Inactive
<span class="status-dot warning"></span> Warning
```

### Alerts

#### Alert Types
```html
<!-- Success alert -->
<div class="alert alert-success" role="alert">
  <i class="fas fa-check-circle me-2"></i>
  Operation completed successfully.
</div>

<!-- Warning alert -->
<div class="alert alert-warning" role="alert">
  <i class="fas fa-exclamation-triangle me-2"></i>
  Please review this before proceeding.
</div>

<!-- Danger alert -->
<div class="alert alert-danger" role="alert">
  <i class="fas fa-times-circle me-2"></i>
  An error occurred. Please try again.
</div>

<!-- Info alert -->
<div class="alert alert-info" role="alert">
  <i class="fas fa-info-circle me-2"></i>
  FYI: This is informational.
</div>
```

### Modals

#### Modal Structure
```html
<div class="modal fade" id="exampleModal" tabindex="-1">
  <div class="modal-dialog modal-lg">
    <div class="modal-content">
      <div class="modal-header">
        <h5 class="modal-title">Modal Title</h5>
        <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
      </div>
      <div class="modal-body">
        Content here
      </div>
      <div class="modal-footer">
        <button class="btn btn-outline-secondary" data-bs-dismiss="modal">Cancel</button>
        <button class="btn btn-primary">Submit</button>
      </div>
    </div>
  </div>
</div>
```

---

## ACCESSIBILITY

### Color Contrast
- Main text on white: minimum 4.5:1 (WCAG AA)
- Dark text on light backgrounds: aim for 7:1+ (WCAG AAA)
- Never rely on color alone to convey information
- Always pair colors with text or icons

### Font Sizes
- Body text minimum: 14px (0.875rem)
- Form labels minimum: 12px (0.75rem) — but 14px preferred
- Avoid sizes below 12px for important content

### Interactive Elements
- Buttons and links should be at least 44px × 44px (touch targets)
- Focus states must be visible: use focus rings, not just color changes
- Use `aria-label` for icon-only buttons

### Semantic HTML
```html
<!-- Use semantic elements, not <div> for everything -->
<nav>Navigation</nav>
<main>Main content</main>
<header>Page header</header>
<footer>Page footer</footer>
<article>Article content</article>
<section>Section of content</section>
<aside>Sidebar/aside content</aside>
```

### ARIA Labels
```html
<!-- Icon-only buttons need labels -->
<button class="btn btn-sm" aria-label="Save changes">
  <i class="fas fa-save"></i>
</button>

<!-- Regions should be labeled -->
<nav aria-label="Main navigation">...</nav>
<div aria-label="Loading indicator">...</div>
```

---

## DARK MODE & THEMES

### Dark Mode Support
The app supports switching between light and dark themes via `data-theme` attribute:

```html
<!-- Light mode (default) -->
<html>

<!-- Dark mode -->
<html data-theme="dark">
```

All CSS variables automatically adjust. You don't need to write separate dark mode styles for custom CSS.

### Skins (Theme Variants)
The app supports 5 different visual skins:

1. **Classic Teal** (default) — Ocean teal & navy
2. **Obsidian Glass** — Marine glass with teal accents
3. **Ledger** — Bold, flat editorial style
4. **Aurora Bento** — Layered aurora gradients
5. **Fable Velvet** — Warm plum & champagne gold

Each skin redefines all CSS variables for consistent theming.

### Using Dark Mode in Custom CSS
```css
/* Light mode (default) */
.my-component {
  background: var(--surface);
  color: var(--text);
}

/* Dark mode */
html[data-theme="dark"] .my-component {
  background: var(--dt-surface-2);
  color: var(--dt-text);
}
```

### Testing Colors in Dark Mode
When creating custom styles:
1. Test in light mode with default Classic theme
2. Test in dark mode with Classic theme
3. Test with at least one other skin (Obsidian or Aurora)
4. Ensure contrast meets WCAG AA minimum

---

## COMMON PATTERNS

### Page Header
```html
<div class="page-header">
  <div class="page-header-left">
    <h4>Page Title</h4>
    <small>Description or breadcrumb</small>
  </div>
  <div>
    <button class="btn btn-primary">Action</button>
  </div>
</div>
```

### Filter Tabs
```html
<div class="filter-tabs mb-3">
  <button class="filter-tab active" data-filter="all">All</button>
  <button class="filter-tab" data-filter="active">Active</button>
  <button class="filter-tab" data-filter="inactive">Inactive</button>
</div>
```

### Stats Grid
```html
<div class="row g-3 mb-4">
  <div class="col-6 col-md-3">
    <div class="stat-card">
      <div class="d-flex align-items-center gap-3">
        <div class="stat-icon" style="background: var(--emerald-light);">
          <i class="fas fa-users text-emerald"></i>
        </div>
        <div>
          <div class="stat-value">1,234</div>
          <div class="stat-label">Total Users</div>
        </div>
      </div>
    </div>
  </div>
  <!-- More stat cards -->
</div>
```

### Access Chips (Multi-select)
```html
<div class="access-chips">
  <label class="access-chip">
    <input type="checkbox" name="dashboard" class="d-none" />
    <span>Dashboard</span>
  </label>
  <label class="access-chip">
    <input type="checkbox" name="reports" class="d-none" />
    <span>Reports</span>
  </label>
</div>
```

### Empty State
```html
<div class="empty-state">
  <i class="fas fa-inbox"></i>
  <p>No data available</p>
  <small>Add your first item to get started</small>
</div>
```

---

## WHAT NOT TO DO

### DON'T: Use Inline Styles
```html
<!-- ❌ WRONG -->
<button style="border-radius: 8px; background: #003B4D; color: #fff;">Click</button>

<!-- ✓ RIGHT -->
<button class="btn btn-primary rounded-md">Click</button>
```

### DON'T: Use Hardcoded Colors
```html
<!-- ❌ WRONG -->
<p style="color: #003B4D;">Text</p>

<!-- ✓ RIGHT -->
<p class="text-navy">Text</p>
```

### DON'T: Override Border Radius on Cards
```html
<!-- ❌ WRONG -->
<div class="card" style="border-radius: 20px;">

<!-- ✓ RIGHT -->
<div class="card rounded-2xl">
```

### DON'T: Use Bootstrap Defaults
```html
<!-- ❌ WRONG -->
<p class="text-dark">Primary text</p>
<div class="bg-light">Background</div>

<!-- ✓ RIGHT -->
<p class="text-navy">Primary text</p>
<div class="bg-surface">Background</div>
```

### DON'T: Create New Color Variables
```css
/* ❌ WRONG */
.my-component {
  color: #45a049;  /* Don't add new colors */
  background: rgb(100, 150, 200);
}

/* ✓ RIGHT */
.my-component {
  color: var(--emerald);  /* Use existing tokens */
  background: var(--surface);
}
```

### DON'T: Ignore Dark Mode
```css
/* ❌ WRONG - doesn't work in dark mode */
.my-component {
  background: #ffffff;
  color: #000000;
}

/* ✓ RIGHT - works in all modes */
.my-component {
  background: var(--surface);
  color: var(--text);
}

html[data-theme="dark"] .my-component {
  background: var(--dt-surface-2);
  color: var(--dt-text);
}
```

### DON'T: Mix Icon Libraries
```html
<!-- ❌ WRONG -->
<i class="fa fa-check"></i>           <!-- Old Font Awesome -->
<i class="icon icon-home"></i>        <!-- Custom icon library -->
<svg>...</svg>                         <!-- Inline SVG -->

<!-- ✓ RIGHT -->
<i class="fas fa-check"></i>          <!-- Font Awesome 6 (fas = solid) -->
```

### DON'T: Use Inconsistent Font Sizes
```html
<!-- ❌ WRONG -->
<p style="font-size: 13px;">Text</p>
<label style="font-size: 0.9rem;">Label</label>

<!-- ✓ RIGHT -->
<p class="fs-85">Text</p>  <!-- 13.6px / 0.85rem -->
<label class="form-label">Label</label>  <!-- 12.8px / 0.8rem -->
```

### DON'T: Add Shadows Inline
```html
<!-- ❌ WRONG -->
<div style="box-shadow: 0 10px 30px rgba(0,0,0,0.1);">

<!-- ✓ RIGHT -->
<div class="shadow-md">
```

---

## QUICK REFERENCE

### Most Common CSS Classes
```html
<!-- Text colors -->
.text-navy .text-muted .text-success .text-warning .text-danger

<!-- Backgrounds -->
.bg-surface .bg-surface-alt .bg-sky-wash

<!-- Spacing -->
.m-0 .m-2 .m-3 .p-2 .p-3 .p-4 .gap-2 .gap-3 .gap-4

<!-- Borders -->
.rounded-md .rounded-lg .rounded-xl

<!-- Shadows -->
.shadow-sm .shadow-md .shadow-lg

<!-- Typography -->
.fw-bold .fw-semibold .fw-medium .small .fs-75 .fs-80 .fs-85

<!-- Components -->
.btn .btn-primary .btn-outline-emerald .card .stat-card .badge .alert
```

### Color Variables by Skin
Each skin redefines all colors. Check the active skin in browser dev tools: look for `[data-skin="X"]` attribute.

### Testing Checklist
- [ ] Tested in light mode
- [ ] Tested in dark mode  
- [ ] Tested with Classic and at least one other skin
- [ ] All text contrast meets WCAG AA (4.5:1 minimum)
- [ ] No inline `style` attributes
- [ ] No hardcoded colors
- [ ] All icons from Font Awesome 6 (fas prefix)
- [ ] Forms use `.form-label` and `.form-control`
- [ ] Buttons use `.btn` and semantic classes
- [ ] Cards use `.card` and `.card-body`

---

## RESOURCES

- **Design System File:** `/wwwroot/css/site.css` (6000+ lines, well-documented)
- **Theme File:** `/wwwroot/css/themes.css` (4 additional skins)
- **Bootstrap 5 Docs:** https://getbootstrap.com/docs/5.3/
- **Font Awesome 6:** https://fontawesome.com/icons
- **WCAG 2.1 Contrast:** https://webaim.org/articles/contrast/

---

**Questions?** Check the inline CSS comments in `/wwwroot/css/site.css` or review existing components in the views.
