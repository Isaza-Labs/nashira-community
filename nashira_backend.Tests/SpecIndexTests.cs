using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Ai.Tools;

namespace nashira_backend.Tests;

// Exercises the OpenAPI YAML parser + operation slicer (no DB, no network).
public class SpecIndexTests
{
    private const string Yaml =
        """
        openapi: 3.0.0
        info:
          title: Demo
        paths:
          /devices:
            get:
              operationId: devices_list
              summary: List devices
              tags: [dcim]
              parameters:
                - name: site
                  in: query
                  required: false
                  description: Site filter
                  schema: { type: string }
            post:
              operationId: devices_create
              summary: Create device
              tags: [dcim]
              requestBody:
                required: true
                content:
                  application/json:
                    schema:
                      type: object
                      properties:
                        name: { type: string }
                        status: { type: string }
              responses:
                "201":
                  description: Created
        """;

    [Fact]
    public void ParseOperations_extracts_methods_ids_and_tags()
    {
        var ops = YamlSpecIndex.ParseOperations("demo", Yaml).ToList();

        Assert.Equal(2, ops.Count);
        Assert.Contains(ops, o => o.OperationId == "devices_list" && o.Method == "GET" && o.Path == "/devices");
        Assert.Contains(ops, o => o.OperationId == "devices_create" && o.Method == "POST");
        Assert.All(ops, o => Assert.Contains("dcim", o.Tags));
    }

    [Fact]
    public void ExtractDetail_returns_parameters_and_request_body()
    {
        var post = OperationYamlSlicer.ExtractDetail(Yaml, "POST", "/devices");
        Assert.NotNull(post.RequestBody);

        var get = OperationYamlSlicer.ExtractDetail(Yaml, "GET", "/devices");
        Assert.Single(get.Parameters);
        Assert.Equal("Created", OperationYamlSlicer.ExtractDetail(Yaml, "POST", "/devices").ResponsePreview);
    }
}
