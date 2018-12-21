if ($env:SignType) {
  $env:SignType
} elseif ($env:SYSTEM_COLLECTIONID -eq '011b8bdf-6d56-4f87-be0d-0092136884d9') {
  'test'
}
