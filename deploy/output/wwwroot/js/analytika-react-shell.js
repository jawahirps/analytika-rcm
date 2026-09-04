(() => {
  const mount = document.getElementById('reactShellMount');
  const ctx = window.__ANALYTIKA_CONTEXT__;

  if (!mount || !ctx || !window.React || !window.ReactDOM) {
    return;
  }

  const e = React.createElement;

  function quickLink(label, href, tone) {
    return e('a', {
      key: label,
      className: `btn btn-sm react-shell-btn react-shell-btn-${tone}`,
      href
    }, label);
  }

  function RoleChip({ value }) {
    return e('span', { className: 'react-shell-chip' }, value);
  }

  function Shell() {
    const [now, setNow] = React.useState(() => new Date());

    React.useEffect(() => {
      const timer = window.setInterval(() => setNow(new Date()), 60000);
      return () => window.clearInterval(timer);
    }, []);

    const links = [];
    links.push(quickLink('Dashboard', '/Home/Dashboard', 'ghost'));
    if (ctx.canAccessRcm) links.push(quickLink('BI Reports', '/Home/RCMDashboard', 'teal'));
    if (ctx.canAccessResubmission) links.push(quickLink('Resubmission', '/Resubmission/Index', 'ghost'));
    if (ctx.canAccessAdmin) links.push(quickLink('Admin', '/Admin/Users', 'ghost'));
    if (ctx.canAccessRcm) links.push(quickLink('Portal Sync', '/Portal/Sync', 'ghost'));

    return e('section', { className: 'react-shell-strip' },
      e('div', { className: 'react-shell-copy' },
        e('div', { className: 'react-shell-eyebrow' }, 'Workspace'),
        e('div', { className: 'react-shell-title' }, `Signed in as ${ctx.displayName}`),
        e('div', { className: 'react-shell-sub' }, `${ctx.roles.length ? ctx.roles.join(' · ') : 'Viewer'} · ${now.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })}`),
        e('div', { className: 'react-shell-roles' }, ctx.roles.map(role => e(RoleChip, { key: role, value: role })))
      ),
      e('div', { className: 'react-shell-actions' }, links)
    );
  }

  const root = ReactDOM.createRoot(mount);
  root.render(e(Shell));
})();
