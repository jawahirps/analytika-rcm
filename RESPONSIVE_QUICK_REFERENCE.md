# Responsive Design Quick Reference - Analytika RCM

## Touch Target Sizes (WCAG 2.5.5 Compliant)

```css
/* Minimum touch targets */
44px × 44px  = Standard (buttons, links, form inputs)
40px × 40px  = Acceptable (pagination, toggles)
36px × 36px  = Desktop only (hover-based UI)
```

## Responsive Breakpoints

```css
/* Mobile First Approach */
< 576px     Mobile phones (375px)
576px-767px Large phones (600px)
768px-991px Tablets (768px)
992px-1023px Laptops (1000px)
≥ 1024px    Desktop/Tablets landscape (1920px)
```

## Common Media Queries in Use

```css
/* Tablet and down (off-canvas sidebar) */
@media (max-width: 991.98px) { }

/* Tablet only */
@media (max-width: 1024px) and (min-width: 768px) { }

/* Mobile only */
@media (max-width: 768px) { }

/* Small mobile only */
@media (max-width: 576px) { }

/* Ultra mobile */
@media (max-width: 480px) { }

/* Touch devices only */
@media (hover: none) and (pointer: coarse) { }

/* Landscape only */
@media (orientation: landscape) { }
```

## Fixed Component Sizes

| Component | Desktop | Tablet/Mobile | Notes |
|-----------|---------|---------------|-------|
| Sidebar width | 260px | 260px (off-canvas) | Hidden on tablet/mobile |
| Sidebar collapsed | 60px | N/A (hidden) | N/A |
| Topbar height | 56px | 56px | Fixed |
| Container max-width | 1600px | 100% | Responsive |
| Modal max-width | 500px | calc(100vw - 32px) | Constrained on mobile |

## Typography Scale

| Element | Desktop | Tablet | Mobile | Ultra-Mobile |
|---------|---------|--------|--------|--------------|
| Page Title | 1.25rem | 1.05rem | 1rem | clamp(1rem, 5vw, 1.25rem) |
| Body Text | 14px | 14px | 15px | 15px |
| Table Headers | 0.75rem | 0.75rem | 0.75rem | 0.75rem |
| Form Labels | 0.8rem | 0.75rem | 0.75rem | 0.75rem |

## Color & Theme

### Light Mode
- Background: `#f7f9f9`
- Text: `#16313c`
- Primary: `#006884`
- Accent: `#54ACBF`

### Dark Mode
```css
html[data-theme="dark"] {
  --bg: #001a24;
  --text: #eaf6f8;
  --primary: #54ACBF;
}
```

## Common Touch Target Issues & Fixes

### ❌ Problem: Small Button
```css
.btn {
  padding: 4px 8px;        /* Too small! */
  height: 24px;            /* Too small! */
}
```

### ✅ Solution
```css
.btn {
  min-height: 44px;        /* Mobile */
  padding: 10px 16px;      /* Adequate spacing */
  display: inline-flex;    /* Align content */
  align-items: center;     /* Vertical center */
}

@media (max-width: 768px) {
  .btn { min-height: 44px; }
}
```

## Sidebar Responsive Pattern

```css
/* Desktop: Fixed sidebar */
.sidebar {
  position: fixed;
  width: 260px;
  left: 0;
  z-index: 1030;
}

.main-wrapper {
  margin-left: 260px;
}

/* Tablet/Mobile: Off-canvas */
@media (max-width: 991.98px) {
  .sidebar {
    transform: translateX(-100%);
    transition: transform 0.3s cubic-bezier(.4,0,.2,1);
  }
  
  .sidebar-open .sidebar {
    transform: translateX(0);
  }
  
  .main-wrapper {
    margin-left: 0;
  }
  
  .sidebar-overlay {
    display: block;
    opacity: 0;
    pointer-events: none;
  }
  
  .sidebar-open .sidebar-overlay {
    opacity: 1;
    pointer-events: auto;
  }
}

/* RTL Support */
[dir="rtl"] .sidebar {
  left: auto;
  right: 0;
  transform: translateX(100%);
}

[dir="rtl"] .sidebar-open .sidebar {
  transform: translateX(0);
}
```

## Form Input Pattern

```css
.form-control,
.form-select {
  min-height: 36px;      /* Desktop */
  padding: 9px 13px;
  font-size: 0.875rem;
  border-radius: 8px;
}

@media (max-width: 768px) {
  .form-control,
  .form-select {
    min-height: 44px;    /* Mobile */
    padding: 10px 13px;
    font-size: 0.875rem;
  }
}
```

## Table Responsive Pattern

```css
.table {
  font-size: 0.825rem;   /* Desktop */
  width: 100%;
}

.table thead th {
  font-size: 0.75rem;
  padding: 12px 16px;
  white-space: nowrap;
}

.table-responsive {
  border-radius: 14px;
  overflow-x: auto;
  -webkit-overflow-scrolling: touch;  /* iOS smooth scroll */
}

@media (max-width: 768px) {
  .table { font-size: 0.85rem; }
  .table thead th { font-size: 0.75rem; }
}

@media (max-width: 576px) {
  .table { font-size: 0.875rem; }
  .table thead th { font-size: 0.78rem; }
}
```

## Modal Pattern

```css
.modal-dialog {
  max-width: 500px;
}

@media (max-width: 576px) {
  .modal-dialog {
    max-width: calc(100vw - 32px);  /* 16px margin each side */
    margin-left: auto;
    margin-right: auto;
  }
  
  .modal-body {
    padding: 20px 16px;
  }
}
```

## Flex Layout Responsive Pattern

```css
/* Horizontal on desktop */
.flex-container {
  display: flex;
  flex-direction: row;
  gap: 16px;
}

/* Vertical on mobile */
@media (max-width: 768px) {
  .flex-container {
    flex-direction: column;
    gap: 12px;
  }
}
```

## Grid Layout Responsive Pattern

```css
/* 3-column on desktop */
.grid {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 16px;
}

/* 2-column on tablet */
@media (max-width: 1024px) {
  .grid {
    grid-template-columns: repeat(2, 1fr);
  }
}

/* 1-column on mobile */
@media (max-width: 768px) {
  .grid {
    grid-template-columns: 1fr;
  }
}
```

## Viewport Meta Tag (Already in Layout)

```html
<meta name="viewport" content="width=device-width, initial-scale=1.0" />
```

## Testing Breakpoints in Chrome DevTools

1. **Open DevTools:** F12
2. **Device Toolbar:** Ctrl+Shift+M
3. **Predefined Devices:**
   - iPhone SE: 375×667
   - iPhone 12 Pro: 390×844
   - Pixel 5: 393×851
   - iPad: 768×1024
   - iPad Pro: 1024×1366

4. **Edit Size:** Click device name to manually enter width

## Common CSS Utilities

```css
/* Responsive visibility */
.d-none { display: none; }
.d-lg-inline { display: inline; }  /* ≥992px */
.d-none.d-lg-inline { }            /* Hidden mobile, visible desktop */

/* Responsive spacing */
.p-3 { padding: 1rem; }
.p-md-3 { padding: 1rem; }  /* ≥768px */
.ms-auto { margin-left: auto; }

/* Responsive text */
.text-center { text-align: center; }
.text-md-left { text-align: left; }  /* ≥768px */

/* Responsive grid */
.col-12 { width: 100%; }           /* Mobile: full width */
.col-md-6 { width: 50%; }          /* ≥768px: half width */
.col-lg-4 { width: 33.333%; }      /* ≥992px: third width */
```

## Performance Tips

1. **Minimize Media Queries:**
   - Use 4-5 key breakpoints, not 20
   - Use mobile-first approach (start small)
   - Use max-width sparingly

2. **Optimize Images:**
   - Use srcset for responsive images
   - Lazy load large images
   - Use WebP with fallback

3. **CSS Organization:**
   - Base styles first (mobile)
   - Media queries at bottom
   - Use CSS variables for values
   - Group related rules together

4. **Testing:**
   - Test on real devices (not just DevTools)
   - Use Chrome Lighthouse
   - Monitor Core Web Vitals
   - Test on slow networks (Throttle in DevTools)

## Debugging Tips

### Check Touch Target Size
```javascript
// In DevTools Console:
const el = document.querySelector('.btn');
const rect = el.getBoundingClientRect();
console.log(`${rect.width}×${rect.height}px`);
// Should be ≥ 44×44px
```

### Check Responsive Breakpoints
```javascript
// In DevTools Console:
const mediaQuery = window.matchMedia('(max-width: 768px)');
console.log(mediaQuery.matches);  // true if mobile, false if desktop
```

### List All Media Queries in CSS
```javascript
// Shows all media query rules
Array.from(document.styleSheets)
  .flatMap(sheet => Array.from(sheet.cssRules || []))
  .filter(rule => rule.media)
  .forEach(rule => console.log(rule.media.mediaText));
```

---

## Quick Checklist

When adding new responsive components:

- [ ] Mobile version designed first
- [ ] Touch targets ≥ 44px on mobile
- [ ] Tested at 375px, 768px, 1920px viewports
- [ ] Proper media queries for breakpoints
- [ ] Dark mode CSS properties set
- [ ] RTL layout considered
- [ ] No horizontal scrollbars
- [ ] Fonts readable (min 14px mobile, 12px desktop)
- [ ] Images responsive (max-width: 100%)
- [ ] Forms and inputs touch-friendly
- [ ] Tested on real devices
- [ ] Lighthouse score > 75 mobile

---

## Resources

- **WCAG 2.5.5 Touch Target:** https://www.w3.org/WAI/WCAG21/Understanding/target-size.html
- **MDN Responsive Design:** https://developer.mozilla.org/en-US/docs/Learn/CSS/CSS_layout/Responsive_Design
- **Chrome DevTools Guide:** https://developer.chrome.com/docs/devtools/device-mode/
- **Bootstrap Responsive:** https://getbootstrap.com/docs/5.3/getting-started/introduction/

---

**Last Updated:** September 2, 2026  
**Maintainer:** Frontend Team  
**Version:** 1.0
