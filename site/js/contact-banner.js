/*
 * Maps the ?sent / ?error query parameter set by contact.php's redirect to a
 * human-readable banner. External file (not inline) so the site's CSP can
 * keep script-src free of 'unsafe-inline'. Only whitelisted, static strings
 * ever reach the DOM, via textContent.
 */
(function () {
  var params = new URLSearchParams(window.location.search);
  var banner = document.getElementById('cf-banner');
  if (!banner) return;
  if (params.get('sent')) {
    banner.className = 'cf-banner cf-ok';
    banner.textContent = 'Thanks — your message has been sent. We will get back to you soon.';
  } else if (params.get('error')) {
    var messages = {
      missing: 'Please fill in your name, email and message.',
      email:   'That email address does not look valid.',
      length:  'One of the fields is too long. Please shorten your message and try again.',
      invalid: 'Your message could not be processed. Please remove any unusual characters and try again.',
      send:    'Sorry, the message could not be sent right now. Please try again later.',
      config:  'The contact form is not available right now. Please try again later.',
      rate:    'You have sent several messages already. Please wait a little while before trying again.',
      captcha: 'Please complete the verification below and try again.',
      method:  'Please use the form below to send your message.'
    };
    banner.className = 'cf-banner cf-err';
    banner.textContent = messages[params.get('error')] || 'Sorry, something went wrong. Please try again.';
  }
})();
