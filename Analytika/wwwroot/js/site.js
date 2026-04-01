// Analytika - Global JavaScript

$(document).ready(function() {
    // Bootstrap dropdown submenu hover
    $('.dropdown-submenu').on('mouseenter', function() {
        $(this).find('> .dropdown-menu').show();
    }).on('mouseleave', function() {
        $(this).find('> .dropdown-menu').hide();
    });

    // Auto-dismiss alerts after 5 seconds
    setTimeout(function() {
        $('.alert-dismissible').fadeOut('slow');
    }, 5000);
});

// Utility: format date as DD/MM/YYYY
function formatDateDDMMYYYY(dateStr) {
    var d = new Date(dateStr);
    return ('0' + d.getDate()).slice(-2) + '/' +
           ('0' + (d.getMonth() + 1)).slice(-2) + '/' +
           d.getFullYear();
}
